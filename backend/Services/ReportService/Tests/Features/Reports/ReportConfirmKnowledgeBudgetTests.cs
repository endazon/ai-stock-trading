using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
using ReportService.Features.Reports.ConfirmReport;
using Wolverine.Tracking;
using Xunit;

namespace ReportService.Tests;

// FR-07, FR-08, FR-14, 計画 ADR-0068 決定6, #1385, IADR-0556 決定 1・3: 確定の後の KB の後処理を時間予算で打ち切る（内側の上限）。
// - KB が返らなくても、確定は予算の後に 200 で返る（確定の可否を KB に左右させない）。確定の通知は出る。
// - 置き換え（確定版の写し）が予算内に終わらなければ、ドラフトの写しは消さない（ADR-0068 決定6 の順序）。
// - レビュー局面は最新の確定版の版番号を返す（通知サービスが時間切れの後に「この版で確定されたか」を照会する）。
public class ReportConfirmKnowledgeBudgetTests
{
    private const string Key = "daily-2026-10-16";

    // 取り消しまで返らない保存の口（基盤の KB が止まった形）。
    private sealed class HangingWriter : IKnowledgeBaseWriter
    {
        public int Calls { get; private set; }

        public bool SawCancellation { get; private set; }

        public async Task<KnowledgeWriteResult> SaveAsync(KnowledgeDocument document, CancellationToken cancellationToken = default)
        {
            Calls++;
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
                throw;
            }

            return KnowledgeWriteResult.Ok(Guid.NewGuid());
        }
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory(
        FakeKnowledgeCatalog catalog, IKnowledgeBaseWriter writer, string? budgetSeconds) =>
        new ReportWorkerWebApplicationFactory().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ReportDraftKnowledgeOptions.EnabledKey] = "true",
                ["Reports:Confirm:KnowledgeBudgetSeconds"] = budgetSeconds,
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IKnowledgeDocumentCatalog>();
                s.AddSingleton<IKnowledgeDocumentCatalog>(catalog);
                s.RemoveAll<IKnowledgeBaseWriter>();
                s.AddSingleton(writer);
            });
        });

    // T-10-2726: 初回の確定で KB の保存が返らない。予算（0.3 秒）で打ち切り、確定は 200（遷移した・版 2）で返る。通知は出る。
    // 保存できていないのでドラフトの写しは消さない。保存には要求の取り消しではなく予算の取り消しが届く。
    [Fact]
    public async Task T_10_2726_KBの保存が返らなくても確定は時間予算の後に200で返りドラフトの写しは消さない()
    {
        var catalog = new FakeKnowledgeCatalog();
        var writer = new HangingWriter();
        await using var factory = Factory(catalog, writer, "0.3");
        ReportKnowledgeReingestTestKit.Seed(factory.Services, Key, ReportKind.Daily, new DateOnly(2026, 10, 16), "本文", confirm: false);
        var draft = catalog.AddDraft(Key, ReportKind.Daily);

        HttpResponseMessage response = null!;
        var watch = Stopwatch.StartNew();
        var session = await factory.Services.ExecuteAndWaitForTestAsync(async () =>
            response = await ReportKnowledgeReingestTestKit.OwnerClient(factory).PostAsJsonAsync($"/reports/{Key}/confirm", new { ExpectedVersion = 1 }));
        watch.Stop();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("transitioned").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("version").GetInt32().Should().Be(2);
        session.Sent.MessagesOf<ReportConfirmed>().Should().ContainSingle();
        writer.Calls.Should().Be(1);
        writer.SawCancellation.Should().BeTrue("予算で打ち切る");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "KB を待ち続けない");
        catalog.Docs.Should().Contain(draft, "確定版の写しを作れなかったのでドラフトの写しは消さない（ADR-0068 決定6）");
        catalog.DeleteCalls.Should().Be(0);
    }

    // T-10-2726: 再確定で KB の一覧が予算より遅い。置き換えは試みず（入れ直しは取り消しで止まる）、確定は 200。ドラフトの写しは消さず、
    // 確定版の写しの本文も古いまま（次の入れ直しが揃える）。
    [Fact]
    public async Task T_10_2726_再確定で置き換えが予算内に終わらなければドラフトの写しを消さない()
    {
        var catalog = new FakeKnowledgeCatalog { BeforeList = () => Task.Delay(TimeSpan.FromSeconds(1)) };
        await using var factory = Factory(catalog, new HangingWriter(), "0.2");
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IReportStore>();
            var v1 = store.UpsertDraft(new TradingReport
            {
                PeriodKey = Key,
                Kind = ReportKind.Daily,
                PeriodStart = new DateOnly(2026, 10, 16),
                AssumptionsVersion = 1,
                PolicySummary = "版2の方針",
                Body = "# 日報\n\n版2の本文",
            }, 0);
            store.Confirm(Key, v1, new DateTimeOffset(2026, 10, 16, 7, 0, 0, TimeSpan.Zero));
            store.StartRevisionDraft(new TradingReport
            {
                PeriodKey = Key,
                Kind = ReportKind.Daily,
                PeriodStart = new DateOnly(2026, 10, 16),
                PolicySummary = "版3の方針",
                Body = "# 日報\n\n版3の本文",
            }, 2).Should().Be(3);
        }

        var confirmedCopy = catalog.AddExisting(Key, "Daily", "版2の本文");
        var draft = catalog.AddDraft(Key, ReportKind.Daily, "版3の下書き");

        HttpResponseMessage response = null!;
        await factory.Services.ExecuteAndWaitForTestAsync(async () =>
            response = await ReportKnowledgeReingestTestKit.OwnerClient(factory).PostAsJsonAsync($"/reports/{Key}/confirm", new { ExpectedVersion = 3 }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        catalog.Docs.Should().Contain(draft, "置き換えが終わっていないのでドラフトの写しは消さない（ADR-0068 決定6 の順序）");
        confirmedCopy.Body.Should().Be("版2の本文", "置き換えは予算内に終わらなかった（次の入れ直しが揃える）");
        catalog.DeleteCalls.Should().Be(0);
    }

    // T-10-2727: 予算の構成。未設定・非数値・非正値・無限は既定 20 秒。小数を受ける。
    [Theory]
    [InlineData(null, 20.0)]
    [InlineData("", 20.0)]
    [InlineData("abc", 20.0)]
    [InlineData("0", 20.0)]
    [InlineData("-3", 20.0)]
    [InlineData("Infinity", 20.0)]
    [InlineData("12", 12.0)]
    [InlineData("0.5", 0.5)]
    public void T_10_2727_KBの後処理の時間予算は未設定と不正値で既定20秒(string? value, double expectedSeconds)
    {
        ReportConfirmKnowledgeBudget.Parse(value).Should().Be(TimeSpan.FromSeconds(expectedSeconds));
        ReportConfirmKnowledgeBudget.DefaultKnowledgeBudgetSeconds.Should().Be(20);
    }

    // T-10-2728: レビュー局面は最新の確定版の版番号を返す（確定前は欠落、確定後は確定版、版 n+1 のドラフトの間は効いている確定版）。
    // gRPC の写しも同じ値を運ぶ（欠落は設定しない）。
    [Fact]
    public async Task T_10_2728_レビュー局面は最新の確定版の版番号を返す()
    {
        var catalog = new FakeKnowledgeCatalog();
        await using var factory = Factory(catalog, new HangingWriter(), "0.1");
        ReportKnowledgeReingestTestKit.Seed(factory.Services, Key, ReportKind.Daily, new DateOnly(2026, 10, 16), "本文", confirm: false);
        var client = ReportKnowledgeReingestTestKit.OwnerClient(factory);

        async Task<JsonElement> ReviewAsync()
        {
            var json = await client.GetStringAsync($"/reports/{Key}/review");
            return JsonDocument.Parse(json).RootElement.Clone();
        }

        var before = await ReviewAsync();
        before.TryGetProperty("latestConfirmedVersion", out var none).Should().BeTrue();
        none.ValueKind.Should().Be(JsonValueKind.Null, "確定前は確定版が無い");

        await factory.Services.ExecuteAndWaitForTestAsync(async () =>
            (await client.PostAsJsonAsync($"/reports/{Key}/confirm", new { ExpectedVersion = 1 })).EnsureSuccessStatusCode());
        var after = await ReviewAsync();
        after.GetProperty("latestConfirmedVersion").GetInt32().Should().Be(2, "下書き 1 の確定は版 2");

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IReportStore>().StartRevisionDraft(new TradingReport
            {
                PeriodKey = Key,
                Kind = ReportKind.Daily,
                PeriodStart = new DateOnly(2026, 10, 16),
                PolicySummary = "版3の方針",
                Body = "# 日報\n\n版3",
            }, 2).Should().Be(3);
        }

        var revision = await ReviewAsync();
        (revision.GetProperty("version").GetInt32(), revision.GetProperty("latestConfirmedVersion").GetInt32()).Should().Be((3, 2));

        using var appScope = factory.Services.CreateScope();
        var view = appScope.ServiceProvider.GetRequiredService<ReportAppService>().GetReviewView(Key)!;
        var wire = ReportOwnerReadWireMapping.ToProto(view);
        (wire.HasLatestConfirmedVersion, wire.LatestConfirmedVersion).Should().Be((true, 2));
    }
}
