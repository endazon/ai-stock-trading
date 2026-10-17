using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.KnowledgeBase;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Infrastructure.ExternalServices;
using Wolverine.Tracking;
using Xunit;

namespace ReportService.Tests;

// FR-07, FR-08, FR-09, 計画 ADR-0068 決定6, ADR-0065 決定1・2・5, #1373, IADR-0549 決定5・6: 再確定の KB と通知を**本番の組み立て**で確かめる。
// - 再確定は確定版の写しを最新の確定版で**置き換える**（作らない＝同じ期間の写しを 2 件にしない）。承認待ちの写しは確定版の後に消す。
// - 確定の通知（ReportConfirmed）は確定した版と、それまで効いていた版を運ぶ。
// - 行が版 n+1 のドラフトの間の入れ直しは、ドラフトではなく確定版を入れる。
// - より新しい日付の日報の確定で確定できなくなった版 n+1 のドラフトの写しは、失効の印つきで残す（削除しない）。
public class ReportReconfirmKnowledgeTests
{
    private const string Key = "daily-2026-10-16";
    private const string NextKey = "daily-2026-10-17";

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory(
        FakeKnowledgeCatalog catalog, RecordingWriter writer, bool draftCopyEnabled = false) =>
        new ReportWorkerWebApplicationFactory().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ReportDraftKnowledgeOptions.EnabledKey] = draftCopyEnabled ? "true" : "false",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IKnowledgeDocumentCatalog>();
                s.AddSingleton<IKnowledgeDocumentCatalog>(catalog);
                s.RemoveAll<IKnowledgeBaseWriter>();
                s.AddSingleton<IKnowledgeBaseWriter>(writer);
            });
        });

    private static HttpClient Owner(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, ReportKnowledgeReingestTestKit.OwnerRole);
        client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, "owner");
        return client;
    }

    // 確定済みの日報（版 2）に版を足したドラフト（版 3）を置く（段 B の /policy が保存層に書くのと同じ）。
    private static void SeedConfirmedThenRevision(IServiceProvider services, string key, DateOnly start)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IReportStore>();
        var v1 = store.UpsertDraft(new TradingReport
        {
            PeriodKey = key,
            Kind = ReportKind.Daily,
            PeriodStart = start,
            AssumptionsVersion = 1,
            PolicySummary = "版2の方針",
            Body = "# 日報\n\n版2の本文",
        }, 0);
        store.Confirm(key, v1, new DateTimeOffset(start.ToDateTime(new TimeOnly(7, 0)), TimeSpan.Zero));
        store.StartRevisionDraft(new TradingReport
        {
            PeriodKey = key,
            Kind = ReportKind.Daily,
            PeriodStart = start,
            PolicySummary = "版3の方針",
            Body = "# 日報\n\n版3の本文",
        }, 2).Should().Be(3);
    }

    // T-10-2697: 再確定は確定版の写しの本文を版 4（最新の確定版）で置き換え、作成しない。承認待ちの写しは確定版の後に消える。
    // ReportConfirmed は Version=4・SupersededVersion=2 を運ぶ。
    [Fact]
    public async Task 再確定は確定版の写しを最新の確定版で置き換え通知は効いている版の変化を運ぶ()
    {
        var catalog = new FakeKnowledgeCatalog();
        var writer = new RecordingWriter();
        await using var factory = Factory(catalog, writer);
        SeedConfirmedThenRevision(factory.Services, Key, new DateOnly(2026, 10, 16));
        var confirmedCopy = catalog.AddExisting(Key, "Daily", "版2の本文");
        catalog.AddDraft(Key, ReportKind.Daily, "版3の下書き");

        HttpResponseMessage response = null!;
        var session = await factory.Services.ExecuteAndWaitForTestAsync(async () =>
            response = await Owner(factory).PostAsJsonAsync($"/reports/{Key}/confirm", new { ExpectedVersion = 3 }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var confirmed = session.Sent.MessagesOf<ReportConfirmed>().Should().ContainSingle().Subject;
        (confirmed.Version, confirmed.SupersededVersion).Should().Be((4, 2));
        writer.Documents.Should().BeEmpty("再確定では確定版の写しを作らない（置き換える）");
        catalog.CreateCalls.Should().Be(0);
        confirmedCopy.Body.Should().Contain("版3の本文", "最新の確定版（版 4）の本文");
        catalog.Docs.Should().ContainSingle("承認待ちの写しは確定版の後に消える").Which.Should().BeSameAs(confirmedCopy);
    }

    // T-10-2698: 初回の確定は従来どおり保存の口（作成）を通り、SupersededVersion は null。
    [Fact]
    public async Task 初回の確定は従来どおり写しを作り通知は版の変化を運ばない()
    {
        var catalog = new FakeKnowledgeCatalog();
        var writer = new RecordingWriter();
        await using var factory = Factory(catalog, writer);
        ReportKnowledgeReingestTestKit.Seed(factory.Services, Key, ReportKind.Daily, new DateOnly(2026, 10, 16), "本文", confirm: false);

        HttpResponseMessage response = null!;
        var session = await factory.Services.ExecuteAndWaitForTestAsync(async () =>
            response = await Owner(factory).PostAsJsonAsync($"/reports/{Key}/confirm", new { ExpectedVersion = 1 }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var confirmed = session.Sent.MessagesOf<ReportConfirmed>().Should().ContainSingle().Subject;
        (confirmed.Version, confirmed.SupersededVersion).Should().Be((2, null));
        writer.Documents.Should().ContainSingle();
    }

    // T-10-2699: 行が版 n+1 のドラフトの間の入れ直しは、確定版（版 2）の本文を入れる（ドラフトの本文を確定版として入れない）。
    [Fact]
    public async Task 版nプラス1のドラフトの間の入れ直しは確定版を入れる()
    {
        var catalog = new FakeKnowledgeCatalog();
        await using var factory = Factory(catalog, new RecordingWriter());
        SeedConfirmedThenRevision(factory.Services, Key, new DateOnly(2026, 10, 16));

        var (response, result, _) = await ReportKnowledgeReingestTestKit.RunAsync(factory, new { all = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        result!.Items.Should().ContainSingle().Which.Outcome.Should().Be(
            ReportService.Features.Reports.ReingestKnowledgeBase.ReportKnowledgeReingestOutcome.Created);
        catalog.CreatedDocuments.Should().ContainSingle().Which.Content.Should().Contain("版2の本文").And.NotContain("版3の本文");
    }

    // T-10-2700: より新しい日付の日報の確定で効いている日報が移ったら、移る前の日報の版 n+1 のドラフトの写しは失効の印つきで残る
    // （reportState=draft のまま・reportDraftStatus=expired・露出は全部除外のまま）。元の承認待ちの写しは消え、入れ直しでも消えない。
    [Fact]
    public async Task 効いている日報が移ったら版nプラス1の写しは失効の印つきで残る()
    {
        var catalog = new FakeKnowledgeCatalog();
        await using var factory = Factory(catalog, new RecordingWriter(), draftCopyEnabled: true);
        SeedConfirmedThenRevision(factory.Services, Key, new DateOnly(2026, 10, 16));
        var pending = catalog.AddDraft(Key, ReportKind.Daily, "版3の下書き");
        ReportKnowledgeReingestTestKit.Seed(factory.Services, NextKey, ReportKind.Daily, new DateOnly(2026, 10, 17), "翌日の本文", confirm: false);

        (await Owner(factory).PostAsJsonAsync($"/reports/{NextKey}/confirm", new { ExpectedVersion = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        catalog.Docs.Should().NotContain(pending);
        var expired = catalog.Docs.Where(d => ReportKnowledgeMapper.IsExpiredDraftCopy(new KnowledgeCatalogEntry(
            d.Id, d.Title, d.Attributes, d.Body != null, d.UpdatedAt))).Should().ContainSingle().Subject;
        expired.Attributes[KnowledgeReportDraftCopy.StateKey].Should().Be(KnowledgeReportDraftCopy.DraftState, "基盤の棚卸しが確定版の写しに数えない");
        KnowledgeExposureAttributes.Keys.Should().OnlyContain(k => expired.Attributes[k] == KnowledgeExposureAttributes.Excluded);
        expired.Body.Should().StartWith("> 失効した報告書のドラフト（版 3）");

        await ReportKnowledgeReingestTestKit.RunAsync(factory, new { all = true });
        catalog.Docs.Should().Contain(expired, "失効の印つきの写しは入れ直しでも消さない");
    }

    // T-10-2701（PR #1375 の AI レビュー 🟡）: 版 n+1 が承認待ち（その日報はまだ効いている）の間の入れ直しは、承認待ちの写しを消さない
    // （確定済みの報告書に「残った」写しとは扱わない）。確定版の写しは確定版（版 2）で作る。
    [Fact]
    public async Task 承認待ちの版nプラス1の写しは入れ直しで消えない()
    {
        var catalog = new FakeKnowledgeCatalog();
        await using var factory = Factory(catalog, new RecordingWriter());
        SeedConfirmedThenRevision(factory.Services, Key, new DateOnly(2026, 10, 16));
        var pending = catalog.AddDraft(Key, ReportKind.Daily, "版3の下書き");

        var (response, result, _) = await ReportKnowledgeReingestTestKit.RunAsync(factory, new { all = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        result!.Items.Should().ContainSingle().Which.DraftCopiesRemoved.Should().Be(0);
        catalog.Docs.Should().Contain(pending, "承認待ちの版 n+1 の写しは残す");
        catalog.CreatedDocuments.Should().ContainSingle().Which.Content.Should().Contain("版2の本文");
    }

    // T-10-2702（PR #1375 の AI レビュー 🟡）: 確定時に印を付けそこねた失効のドラフト（効いている日報が移った版 n+1 の印の無い写し）は、
    // 入れ直しが失効の印つきの写しに置き換える（消さない・2 回目は作らない）。
    [Fact]
    public async Task 印の無い失効ドラフトの写しは入れ直しが失効にする()
    {
        var catalog = new FakeKnowledgeCatalog();
        await using var factory = Factory(catalog, new RecordingWriter());
        SeedConfirmedThenRevision(factory.Services, Key, new DateOnly(2026, 10, 16));
        ReportKnowledgeReingestTestKit.Seed(factory.Services, NextKey, ReportKind.Daily, new DateOnly(2026, 10, 17), "翌日の本文");
        var pending = catalog.AddDraft(Key, ReportKind.Daily, "版3の下書き");

        await ReportKnowledgeReingestTestKit.RunAsync(factory, new { all = true });

        catalog.Docs.Should().NotContain(pending);
        var expired = catalog.Docs.Where(d => ReportKnowledgeMapper.IsExpiredDraftCopy(new KnowledgeCatalogEntry(
            d.Id, d.Title, d.Attributes, d.Body != null, d.UpdatedAt))).Should().ContainSingle().Subject;
        expired.Attributes[KnowledgeReportDraftCopy.StateKey].Should().Be(KnowledgeReportDraftCopy.DraftState);
        expired.Body.Should().StartWith("> 失効した報告書のドラフト（版 3）");

        var createsBefore = catalog.CreateCalls;
        await ReportKnowledgeReingestTestKit.RunAsync(factory, new { all = true });
        catalog.CreateCalls.Should().Be(createsBefore, "2 回目は印つきの写しを作らない（承認待ちの写しが無い）");
        catalog.Docs.Should().Contain(expired);
    }

    private sealed class RecordingWriter : IKnowledgeBaseWriter
    {
        public List<KnowledgeDocument> Documents { get; } = [];

        public Task<KnowledgeWriteResult> SaveAsync(KnowledgeDocument document, CancellationToken cancellationToken = default)
        {
            Documents.Add(document);
            return Task.FromResult(KnowledgeWriteResult.Ok(Guid.NewGuid()));
        }
    }
}
