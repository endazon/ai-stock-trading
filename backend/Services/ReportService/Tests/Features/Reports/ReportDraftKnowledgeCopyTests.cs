using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.KnowledgeBase;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Features.Reports.ReingestKnowledgeBase;
using ReportService.Infrastructure.ExternalServices;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-08, UC-03, #1300, IADR-0526（planning#784 の利用者裁定 2026-10-10）: 承認待ちの報告書の写し（ドラフト）を KB に 1 件だけ持ち、
// 確定で置き換える。受け入れ基準の写像:
//   - 写しの属性は露出の 3 キーを必ず excluded で持ち、coverage=market を持たない。表題は確定版と分かれる（写像）。
//   - 初回は作り、以後は同じ文書の本文を差し替える（再起動の後は一覧から探す）。一覧を引けなければ作らない。
//   - 確定で写しを消す（確定版を作れたときだけ）。機能の門（既定 false）が閉じていれば何も送らない。
//   - KB の失敗・例外で報告書の生成・確定を止めない。
//   - 入れ直しは写しを確定版の写しに数えない（数えると確定版を作らない）。確定済みに残った写しは消す。
public class ReportDraftKnowledgeCopyTests
{
    private const string PeriodKey = "daily-2026-10-09";

    private static TradingReport Report(string body = "# 日報\n\n本文 1", string periodKey = PeriodKey, ReportKind kind = ReportKind.Daily) => new()
    {
        PeriodKey = periodKey,
        Kind = kind,
        PeriodStart = new DateOnly(2026, 10, 9),
        AssumptionsVersion = 1,
        PolicySummary = "押し目買い",
        Body = body,
    };

    private static CatalogReportDraftKnowledgeCopy Copy(IKnowledgeDocumentCatalog catalog, bool enabled = true) =>
        new(catalog, new ReportDraftKnowledgeOptions(enabled), NullLogger<CatalogReportDraftKnowledgeCopy>.Instance);

    private static void ShouldBeExcludedEverywhere(IReadOnlyDictionary<string, string> attributes)
    {
        attributes.Should().Contain(KnowledgeExposureAttributes.SearchKey, KnowledgeExposureAttributes.Excluded);
        attributes.Should().Contain(KnowledgeExposureAttributes.GraphKey, KnowledgeExposureAttributes.Excluded);
        attributes.Should().Contain(KnowledgeExposureAttributes.AiInputKey, KnowledgeExposureAttributes.Excluded);
        KnowledgeExposureAttributes.IsAllExcluded(attributes).Should().BeTrue();
    }

    // ---- 写像 ----

    // FR-08, #1300, IADR-0526 決定 1: 写しの属性は露出の 3 キーを全部 excluded で持ち、目印 coverage=market を持たない（取引判断の 2 本目の検索に乗らない）。
    [Theory]
    [InlineData(ReportKind.Daily, "daily-2026-10-09")]
    [InlineData(ReportKind.Weekly, "weekly-2026-W41")]
    [InlineData(ReportKind.Monthly, "monthly-2026-10")]
    public void 写しの属性は露出の3キーを全部除外にし市場の目印を持たない(ReportKind kind, string periodKey)
    {
        var document = ReportKnowledgeMapper.ToDraftDocument(Report(periodKey: periodKey, kind: kind), version: 3);

        ShouldBeExcludedEverywhere(document.Attributes!);
        document.Attributes.Should().Contain(KnowledgeReportDraftCopy.StateKey, KnowledgeReportDraftCopy.DraftState);
        document.Attributes.Should().Contain(ReportKnowledgeMapper.PeriodKeyAttribute, periodKey);
        document.Attributes.Should().Contain(ReportKnowledgeMapper.KindAttribute, kind.ToString());
        document.Attributes.Should().NotContainKey(KnowledgeSearchAttributes.Coverage);
        document.Confidentiality.Should().Be(KnowledgeConfidentiality.Internal);
        document.ContentType.Should().Be("text/markdown");
        // 確定版の写像（ToDocument）は従来どおり露出のキーを持たず、目印を持つ（確定版は検索・RAG の対象）。
        var confirmed = ReportKnowledgeMapper.ToDocument(Report(periodKey: periodKey, kind: kind));
        KnowledgeExposureAttributes.Keys.Should().AllSatisfy(k => confirmed.Attributes.Should().NotContainKey(k));
        confirmed.Attributes.Should().Contain(KnowledgeSearchAttributes.Coverage, KnowledgeSearchAttributes.MarketCoverage);
    }

    // FR-08, #1300, IADR-0526 決定 1: 表題は確定版と分ける（入れ直し・MSP の写しの棚卸しは確定版の表題との完全一致を目印に使う）。
    [Theory]
    [InlineData(ReportKind.Daily, "daily-2026-10-09")]
    [InlineData(ReportKind.Weekly, "weekly-2026-W41")]
    [InlineData(ReportKind.Monthly, "monthly-2026-10")]
    public void 写しの表題は確定版の表題と分かれ取引判断の目印で見分けられる(ReportKind kind, string periodKey)
    {
        var draftTitle = ReportKnowledgeMapper.DraftTitleOf(kind, periodKey);

        draftTitle.Should().Be($"報告書ドラフト {kind} {periodKey}");
        draftTitle.Should().NotBe(ReportKnowledgeMapper.TitleOf(kind, periodKey));
        draftTitle.Should().NotStartWith("確定報告書");
        KnowledgeReportDraftCopy.IsDraftTitle(draftTitle).Should().BeTrue();
        KnowledgeReportDraftCopy.IsDraftTitle(ReportKnowledgeMapper.TitleOf(kind, periodKey)).Should().BeFalse();
    }

    // FR-06, #1300, IADR-0526 決定 2: 本文の先頭に承認待ち・版を書く。本文が空（月報の初回）なら方針を載せる。
    [Fact]
    public void 写しの本文は承認待ちと版を先頭に書き空なら方針を載せる()
    {
        ReportKnowledgeMapper.DraftBodyOf(Report(body: "# 日報\n\n本文"), version: 4)
            .Should().StartWith("> 承認待ちの報告書（ドラフト・版 4）。").And.EndWith("# 日報\n\n本文");
        ReportKnowledgeMapper.DraftBodyOf(Report(body: string.Empty), version: 1)
            .Should().Contain("## 翌期間の方針").And.Contain("押し目買い");
    }

    // ---- 作成・差し替え ----

    // FR-06, FR-08, UC-03, #1300, IADR-0526 決定 2: 初回は作り、2 回目は同じ文書の本文を差し替える（KB の写しは常に 1 件）。
    [Fact]
    public async Task 初回は写しを作り次の版は同じ文書の本文を差し替える()
    {
        var catalog = new FakeKnowledgeCatalog();
        var copy = Copy(catalog);

        await copy.PublishAsync(Report(body: "本文 1"), version: 1);
        await copy.PublishAsync(Report(body: "本文 2"), version: 2);

        catalog.CreateCalls.Should().Be(1);
        catalog.PutCalls.Should().Be(1);
        var doc = catalog.Docs.Should().ContainSingle().Which;
        doc.Title.Should().Be("報告書ドラフト Daily daily-2026-10-09");
        doc.Body.Should().Contain("版 2").And.EndWith("本文 2");
        ShouldBeExcludedEverywhere(doc.Attributes);
        // 🔴 送ったすべての作成の要求が露出の 3 キーを持つ（欠けると基盤がその用途で索引する）。
        catalog.CreatedDocuments.Should().AllSatisfy(d => ShouldBeExcludedEverywhere(d.Attributes!));
    }

    // FR-06, #1300, IADR-0526 決定 2: 覚えていない（再起動・別の複製）なら一覧から属性で探して差し替える（作らない）。
    [Fact]
    public async Task 覚えていなければ一覧から写しを探して差し替え新しく作らない()
    {
        var catalog = new FakeKnowledgeCatalog();
        var existing = catalog.AddDraft(PeriodKey, ReportKind.Daily, body: "古い版");
        // 同じ期間の確定版の写し・別の期間の写しは対象外。
        catalog.AddExisting(PeriodKey, "Daily", body: "確定版");
        var other = catalog.AddDraft("daily-2026-10-08", ReportKind.Daily, body: "別の期間");

        await Copy(catalog).PublishAsync(Report(body: "新しい版"), version: 5);

        catalog.CreateCalls.Should().Be(0);
        existing.Body.Should().EndWith("新しい版");
        other.Body.Should().Be("別の期間");
        catalog.Docs.Should().HaveCount(3);
    }

    // FR-06, #1300, IADR-0526 決定 2: 写しが 2 件以上（作成の結果が不明で増えた）なら一番新しいものへ書き、残りを消して 1 件にする。
    [Fact]
    public async Task 写しが重複していたら新しいほうへ書き残りを消す()
    {
        var catalog = new FakeKnowledgeCatalog();
        var older = catalog.AddDraft(PeriodKey, ReportKind.Daily, updatedAt: DateTimeOffset.UtcNow.AddHours(-2));
        var newer = catalog.AddDraft(PeriodKey, ReportKind.Daily, updatedAt: DateTimeOffset.UtcNow.AddHours(-1));

        await Copy(catalog).PublishAsync(Report(body: "最新"), version: 3);

        catalog.Docs.Should().ContainSingle().Which.Id.Should().Be(newer.Id);
        newer.Body.Should().EndWith("最新");
        catalog.Docs.Should().NotContain(older);
    }

    // 🔴 FR-06, #1300, IADR-0526 決定 2（否定形）: 一覧を引けなければ作らない（既にある写しが見えないまま作ると 2 件になる）。
    [Fact]
    public async Task 一覧を引けなければ写しを作らない()
    {
        var catalog = new FakeKnowledgeCatalog { ListOverride = KnowledgeCatalogListResult.Unknown("タイムアウト") };

        await Copy(catalog).PublishAsync(Report(), version: 1);

        catalog.CreateCalls.Should().Be(0);
        catalog.Docs.Should().BeEmpty();
    }

    // FR-06, #1300, IADR-0526 決定 2: 作成の結果が不明なら覚えない。次の回は一覧で見つけて差し替える（重複させない）。
    [Fact]
    public async Task 作成の結果が不明なら次の回は一覧で見つけて差し替える()
    {
        var catalog = new FakeKnowledgeCatalog();
        catalog.CreateBehavior[PeriodKey] = "saved-unknown";
        var copy = Copy(catalog);

        await copy.PublishAsync(Report(body: "本文 1"), version: 1);
        catalog.CreateBehavior.Clear();
        await copy.PublishAsync(Report(body: "本文 2"), version: 2);

        catalog.CreateCalls.Should().Be(1);
        catalog.Docs.Should().ContainSingle().Which.Body.Should().EndWith("本文 2");
    }

    // FR-06, #1300, IADR-0526 決定 2: 覚えていた写しが消されていたら（404）一覧から探し直し、無ければ作り直す。
    [Fact]
    public async Task 覚えていた写しが消されていたら作り直す()
    {
        var catalog = new FakeKnowledgeCatalog();
        var copy = Copy(catalog);
        await copy.PublishAsync(Report(body: "本文 1"), version: 1);
        catalog.Docs.Clear();

        await copy.PublishAsync(Report(body: "本文 2"), version: 2);

        catalog.CreateCalls.Should().Be(2);
        catalog.Docs.Should().ContainSingle().Which.Body.Should().EndWith("本文 2");
    }

    // ---- 確定で消す ----

    // FR-06, FR-08, UC-03, #1300, IADR-0526 決定 3: 確定で写しを消す。確定版の写しと別の期間の写しは残す。
    [Fact]
    public async Task 確定すると写しを消し確定版と別の期間の写しは残す()
    {
        var catalog = new FakeKnowledgeCatalog();
        var copy = Copy(catalog);
        await copy.PublishAsync(Report(), version: 1);
        var confirmed = catalog.AddExisting(PeriodKey, "Daily", body: "確定版");
        var other = catalog.AddDraft("daily-2026-10-08", ReportKind.Daily);

        await copy.RemoveAsync(ReportKind.Daily, PeriodKey);

        catalog.Docs.Should().BeEquivalentTo([confirmed, other]);
    }

    // FR-06, #1300, IADR-0526 決定 5: 機能の門が閉じていれば（既定）作成・差し替え・削除のどれも送らない＝従来の挙動。
    [Fact]
    public async Task 門が閉じていれば何も送らない()
    {
        var catalog = new FakeKnowledgeCatalog();
        catalog.AddDraft(PeriodKey, ReportKind.Daily);
        var copy = Copy(catalog, enabled: false);

        await copy.PublishAsync(Report(), version: 1);
        await copy.RemoveAsync(ReportKind.Daily, PeriodKey);

        (catalog.ListCalls, catalog.CreateCalls, catalog.PutCalls, catalog.DeleteCalls).Should().Be((0, 0, 0, 0));
        ReportDraftKnowledgeOptions.Read(null).Enabled.Should().BeFalse("既定は無効");
        ReportDraftKnowledgeOptions.Read("not-a-bool").Enabled.Should().BeFalse();
        ReportDraftKnowledgeOptions.Read("true").Enabled.Should().BeTrue();
    }

    // FR-06, #1300, IADR-0526 決定 5: KB が例外を投げても、作成・削除の失敗でも呼び出し元へ例外を出さない（報告書を止めない）。
    [Fact]
    public async Task KBの例外や失敗は呼び出し元へ伝えない()
    {
        var throwing = Copy(new ThrowingCatalog());
        await throwing.Invoking(c => c.PublishAsync(Report(), 1)).Should().NotThrowAsync();
        await throwing.Invoking(c => c.RemoveAsync(ReportKind.Daily, PeriodKey)).Should().NotThrowAsync();

        var catalog = new FakeKnowledgeCatalog();
        var draft = catalog.AddDraft(PeriodKey, ReportKind.Daily);
        catalog.DeleteOverride[draft.Id] = KnowledgeCatalogWriteResult.Unknown("タイムアウト");
        await Copy(catalog).Invoking(c => c.RemoveAsync(ReportKind.Daily, PeriodKey)).Should().NotThrowAsync();
        catalog.DeleteCalls.Should().Be(1);
    }

    // ---- 経路（本番の組み立て） ----

    // FR-06, FR-08, UC-03, #1300, IADR-0526 決定 2・3・5: 本番の組み立てで、月報の初回を承認待ちにすると写しができ、
    // 確定すると確定版が作られた後に写しが消える。構成 ReportDraftKnowledge:Enabled で有効にする。
    [Fact]
    public async Task 承認待ちにすると写しができ確定すると確定版の後に写しが消える()
    {
        var catalog = new FakeKnowledgeCatalog();
        var writer = new RecordingWriter();
        await using var factory = Factory(catalog, writer, enabled: true);
        var client = Owner(factory);

        var created = await client.PostAsync("/reports/monthly-bootstrap", content: null);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var started = (await created.Content.ReadFromJsonAsync<BootstrapStartDto>())!;

        var draft = catalog.Docs.Should().ContainSingle().Which;
        draft.Title.Should().Be($"報告書ドラフト Monthly {started.PeriodKey}");
        ShouldBeExcludedEverywhere(draft.Attributes);
        draft.Body.Should().Contain("## 翌期間の方針");

        (await client.PostAsJsonAsync($"/reports/{started.PeriodKey}/confirm", new { ExpectedVersion = started.Version }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        writer.Documents.Should().ContainSingle().Which.Title.Should().Be($"確定報告書 Monthly {started.PeriodKey}");
        catalog.Docs.Should().BeEmpty("確定版で置き換えた（写しは消した）");
    }

    // FR-06, FR-08, #1300, IADR-0526 決定 2: 手の経路（PUT で下書き → POST present）で承認待ちにしても写しを持つ。冪等な再提示では送り直さない。
    [Fact]
    public async Task 手で承認待ちにしたときも写しを持ち冪等な再提示では送らない()
    {
        var catalog = new FakeKnowledgeCatalog();
        await using var factory = Factory(catalog, new RecordingWriter(), enabled: true);
        var client = Owner(factory);

        (await client.PutAsJsonAsync("/reports/daily-2026-07-10", new
        {
            Kind = "Daily",
            PeriodStart = "2026-07-10",
            BasedOn = (string?)null,
            AssumptionsVersion = 1,
            PolicySummary = "翌営業日は押し目買い",
            ExpectedVersion = 0,
        })).IsSuccessStatusCode.Should().BeTrue();
        (await client.PostAsJsonAsync("/reports/daily-2026-07-10/present", new { ExpectedVersion = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/reports/daily-2026-07-10/present", new { ExpectedVersion = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var draft = catalog.Docs.Should().ContainSingle().Which;
        draft.Title.Should().Be("報告書ドラフト Daily daily-2026-07-10");
        draft.Body.Should().Contain("版 1").And.Contain("翌営業日は押し目買い");
        catalog.CreateCalls.Should().Be(1);
        catalog.PutCalls.Should().Be(0, "冪等な再提示（遷移なし）では送らない");
    }

    // FR-06, #1300, IADR-0526 決定 5（否定形）: 既定（構成なし）では承認待ちにしても確定しても KB の台帳へ何も送らない。
    [Fact]
    public async Task 既定の構成では承認待ちにしても確定しても台帳へ送らない()
    {
        var catalog = new FakeKnowledgeCatalog();
        var writer = new RecordingWriter();
        await using var factory = Factory(catalog, writer, enabled: null);
        var client = Owner(factory);

        var started = (await (await client.PostAsync("/reports/monthly-bootstrap", content: null))
            .Content.ReadFromJsonAsync<BootstrapStartDto>())!;
        (await client.PostAsJsonAsync($"/reports/{started.PeriodKey}/confirm", new { ExpectedVersion = started.Version }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (catalog.ListCalls, catalog.CreateCalls, catalog.PutCalls, catalog.DeleteCalls).Should().Be((0, 0, 0, 0));
        writer.Documents.Should().ContainSingle("確定版の保存は従来どおり");
    }

    // FR-06, #1300, IADR-0526 決定 3（否定形）: 確定版を KB へ保存できなかったら写しを消さない（確定した本文を読める写しを残す）。
    [Fact]
    public async Task 確定版を保存できなければ写しを消さない()
    {
        var catalog = new FakeKnowledgeCatalog();
        var writer = new RecordingWriter { Saved = false };
        await using var factory = Factory(catalog, writer, enabled: true);
        var client = Owner(factory);

        var started = (await (await client.PostAsync("/reports/monthly-bootstrap", content: null))
            .Content.ReadFromJsonAsync<BootstrapStartDto>())!;
        (await client.PostAsJsonAsync($"/reports/{started.PeriodKey}/confirm", new { ExpectedVersion = started.Version }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        catalog.Docs.Should().ContainSingle();
        catalog.DeleteCalls.Should().Be(0);
    }

    // ---- 入れ直し ----

    // 🔴 FR-08, #1300, IADR-0526 決定 4（PR #1301 の監査で改めた）: 写し（ドラフト）は `reportState=draft` **かつ** 表題 `報告書ドラフト …` の文書だけ。
    // 露出の 3 キーが全部 excluded でも、それだけでは写しにしない —— 基盤では露出を全部除外にするのが文書を隠す通常の操作であり、
    // 管理者が隠した確定版の写しを「残った写し」と読むと、入れ直しが消して検索に出る写しを作り直す。
    [Fact]
    public void 写しの判定は状態の属性と表題の両方で見て管理者が隠した確定版の写しは写しとしない()
    {
        var draft = Entry(ReportKnowledgeMapper.DraftTitleOf(ReportKind.Daily, PeriodKey),
            new(ReportKnowledgeMapper.DraftAttributesOf(ReportKind.Daily, PeriodKey), StringComparer.Ordinal) { ["project"] = "ai-stock-trading" });
        var confirmedCopy = Entry(ReportKnowledgeMapper.TitleOf(ReportKind.Daily, PeriodKey),
            new(StringComparer.Ordinal) { ["periodKey"] = PeriodKey, ["kind"] = "Daily", ["project"] = "ai-stock-trading" });
        var hiddenConfirmed = Entry(ReportKnowledgeMapper.TitleOf(ReportKind.Daily, PeriodKey), HiddenConfirmedAttributes());
        var stateOnly = Entry(ReportKnowledgeMapper.TitleOf(ReportKind.Daily, PeriodKey), new(StringComparer.Ordinal)
        {
            ["periodKey"] = PeriodKey,
            ["kind"] = "Daily",
            ["project"] = "ai-stock-trading",
            ["reportState"] = "draft",
        });
        var titleOnly = Entry(ReportKnowledgeMapper.DraftTitleOf(ReportKind.Daily, PeriodKey), new(StringComparer.Ordinal)
        {
            ["periodKey"] = PeriodKey,
            ["kind"] = "Daily",
            ["project"] = "ai-stock-trading",
        });

        ReportKnowledgeMapper.IsDraftCopy(draft).Should().BeTrue();
        ReportKnowledgeMapper.IsDraftCopy(confirmedCopy).Should().BeFalse();
        ReportKnowledgeMapper.IsDraftCopy(hiddenConfirmed).Should().BeFalse("管理者が露出を全部除外にして隠した確定版の写しは写しではない");
        ReportKnowledgeMapper.IsDraftCopy(stateOnly).Should().BeFalse("表題が確定版なら写しではない");
        ReportKnowledgeMapper.IsDraftCopy(titleOnly).Should().BeFalse("reportState=draft が無ければ写しではない");
        ReportKnowledgeMapper.IsDraftCopyOf(draft, ReportKind.Daily, PeriodKey).Should().BeTrue();
        ReportKnowledgeMapper.IsDraftCopyOf(draft, ReportKind.Weekly, PeriodKey).Should().BeFalse();
        ReportKnowledgeMapper.IsDraftCopyOf(draft, ReportKind.Daily, "daily-2026-10-08").Should().BeFalse();
    }

    // FR-08, #1300, IADR-0526 決定 2・4（PR #1301 の監査）: 本システムが作った写しだけを扱う —— project が ai-stock-trading でない・無い写しは
    // 差し替えも削除もしない（別のユニットの文書・#665 より前の形を取り違えない）。
    [Theory]
    [InlineData(null)]
    [InlineData("other-project")]
    public void 写しはプロジェクトが本システムのものだけを対象にする(string? project)
    {
        var attributes = new Dictionary<string, string>(ReportKnowledgeMapper.DraftAttributesOf(ReportKind.Daily, PeriodKey), StringComparer.Ordinal);
        if (project is not null)
            attributes["project"] = project;
        var entry = Entry(ReportKnowledgeMapper.DraftTitleOf(ReportKind.Daily, PeriodKey), attributes);

        ReportKnowledgeMapper.IsDraftCopy(entry).Should().BeTrue("形は写し");
        ReportKnowledgeMapper.IsDraftCopyOf(entry, ReportKind.Daily, PeriodKey).Should().BeFalse("本システムの project ではない");
    }

    // 🔴 FR-08, #1300, IADR-0526 決定 4（PR #1301 の監査）: 管理者が露出を全部除外にして隠した確定版の写しは、入れ直しで確定版の写しとして数え
    // （AlreadyPresent）、消さず、新しく作らない（隠した操作を入れ直しが覆さない）。
    [Fact]
    public async Task 入れ直しは管理者が隠した確定版の写しを消さず作り直さない()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        var catalog = new FakeKnowledgeCatalog();
        var hidden = new FakeKnowledgeCatalog.Doc
        {
            Title = ReportKnowledgeMapper.TitleOf(ReportKind.Daily, PeriodKey),
            Attributes = HiddenConfirmedAttributes(),
            Body = "確定版",
        };
        catalog.Docs.Add(hidden);
        await using var factory = ReportKnowledgeReingestTestKit.WithCatalog(baseFactory, catalog);
        ReportKnowledgeReingestTestKit.Seed(factory.Services, PeriodKey, ReportKind.Daily, new DateOnly(2026, 10, 9), "確定した本文");

        var (_, result, _) = await ReportKnowledgeReingestTestKit.RunAsync(factory, new { all = true });

        var item = result!.Items.Should().ContainSingle().Which;
        item.Outcome.Should().Be(ReportKnowledgeReingestOutcome.AlreadyPresent);
        item.DocumentId.Should().Be(hidden.Id);
        item.DraftCopiesRemoved.Should().Be(0);
        (catalog.CreateCalls, catalog.DeleteCalls).Should().Be((0, 0));
        catalog.Docs.Should().ContainSingle().Which.Should().BeSameAs(hidden);
    }

    private static Dictionary<string, string> HiddenConfirmedAttributes() => new(StringComparer.Ordinal)
    {
        ["periodKey"] = PeriodKey,
        ["kind"] = "Daily",
        ["project"] = "ai-stock-trading",
        ["coverage"] = "market",
        ["search_exposure"] = "excluded",
        ["graph_exposure"] = "excluded",
        ["ai_input"] = "excluded",
    };

    // 🔴 FR-08, #1300, IADR-0526 決定 4: 確定済みの報告書に確定版の写しとドラフトの写しが両方あれば、確定版の写しだけを数え（重複にしない）、写しは消す。
    [Fact]
    public async Task 入れ直しは確定版の写しだけを数え写しは重複に数えず消す()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        var catalog = new FakeKnowledgeCatalog();
        var confirmed = catalog.AddExisting(PeriodKey, "Daily", body: "確定版");
        catalog.AddDraft(PeriodKey, ReportKind.Daily, updatedAt: DateTimeOffset.UtcNow.AddHours(1));
        await using var factory = ReportKnowledgeReingestTestKit.WithCatalog(baseFactory, catalog);
        ReportKnowledgeReingestTestKit.Seed(factory.Services, PeriodKey, ReportKind.Daily, new DateOnly(2026, 10, 9), "確定した本文");

        var (_, result, _) = await ReportKnowledgeReingestTestKit.RunAsync(factory, new { all = true });

        var item = result!.Items.Should().ContainSingle().Which;
        item.Outcome.Should().Be(ReportKnowledgeReingestOutcome.AlreadyPresent);
        item.DocumentId.Should().Be(confirmed.Id);
        item.MatchedCopies.Should().Be(1);
        result.DuplicatesInKb.Should().Be(0);
        result.DraftCopiesRemoved.Should().Be(1);
        catalog.Docs.Should().ContainSingle().Which.Should().BeSameAs(confirmed);
    }

    // FR-08, #1300, IADR-0526 決定 4: 確定済みの報告書に写しだけが残っていたら、確定版を作ってから写しを消す。
    [Fact]
    public async Task 入れ直しは写しだけが残った確定済みの報告書に確定版を作り写しを消す()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        var catalog = new FakeKnowledgeCatalog();
        var draft = catalog.AddDraft(PeriodKey, ReportKind.Daily, body: "承認待ちの本文");
        await using var factory = ReportKnowledgeReingestTestKit.WithCatalog(baseFactory, catalog);
        ReportKnowledgeReingestTestKit.Seed(factory.Services, PeriodKey, ReportKind.Daily, new DateOnly(2026, 10, 9), "確定した本文");

        var (response, result, _) = await ReportKnowledgeReingestTestKit.RunAsync(factory, new { all = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var item = result!.Items.Should().ContainSingle().Which;
        item.Outcome.Should().Be(ReportKnowledgeReingestOutcome.Created);
        item.MatchedCopies.Should().Be(0);
        item.DraftCopiesRemoved.Should().Be(1);
        result.DraftCopiesRemoved.Should().Be(1);
        result.DuplicatesInKb.Should().Be(0);
        catalog.Docs.Should().ContainSingle().Which.Title.Should().Be($"確定報告書 Daily {PeriodKey}");
        catalog.Docs.Should().NotContain(draft);
    }

    // FR-08, #1300, IADR-0526 決定 4（否定形）: 確定版を作れなかったら写しを消さない（確定した本文を読める写しを 1 つも無くさない）。
    [Fact]
    public async Task 入れ直しは確定版を作れなければ写しを消さない()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        var catalog = new FakeKnowledgeCatalog();
        catalog.AddDraft(PeriodKey, ReportKind.Daily);
        catalog.CreateBehavior[PeriodKey] = "failed";
        await using var factory = ReportKnowledgeReingestTestKit.WithCatalog(baseFactory, catalog);
        ReportKnowledgeReingestTestKit.Seed(factory.Services, PeriodKey, ReportKind.Daily, new DateOnly(2026, 10, 9), "確定した本文");

        var (_, result, _) = await ReportKnowledgeReingestTestKit.RunAsync(factory, new { all = true });

        result!.Items.Should().ContainSingle().Which.Outcome.Should().Be(ReportKnowledgeReingestOutcome.Failed);
        result.DraftCopiesRemoved.Should().Be(0);
        catalog.DeleteCalls.Should().Be(0);
    }

    private static KnowledgeCatalogEntry Entry(string title, Dictionary<string, string> attributes) =>
        new(Guid.NewGuid(), title, attributes, HasStoredBody: true, DateTimeOffset.UtcNow);

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory(
        FakeKnowledgeCatalog catalog, RecordingWriter writer, bool? enabled)
    {
        var settings = new Dictionary<string, string?> { ["Reports:Bootstrap:Watchlist:0"] = "AAPL" };
        if (enabled is { } on)
            settings[ReportDraftKnowledgeOptions.EnabledKey] = on ? "true" : "false";

        return new ReportWorkerWebApplicationFactory().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(settings));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IKnowledgeDocumentCatalog>();
                s.AddSingleton<IKnowledgeDocumentCatalog>(catalog);
                s.RemoveAll<IKnowledgeBaseWriter>();
                s.AddSingleton<IKnowledgeBaseWriter>(writer);
            });
        });
    }

    private static HttpClient Owner(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, ReportKnowledgeReingestTestKit.OwnerRole);
        return client;
    }

    private sealed record BootstrapStartDto(string PeriodKey, int Version, bool Presented, bool NotificationFailed);

    private sealed class RecordingWriter : IKnowledgeBaseWriter
    {
        public bool Saved { get; init; } = true;

        public List<KnowledgeDocument> Documents { get; } = [];

        public Task<KnowledgeWriteResult> SaveAsync(KnowledgeDocument document, CancellationToken cancellationToken = default)
        {
            Documents.Add(document);
            return Task.FromResult(Saved ? KnowledgeWriteResult.Ok(Guid.NewGuid()) : KnowledgeWriteResult.NotSaved);
        }
    }

    private sealed class ThrowingCatalog : IKnowledgeDocumentCatalog
    {
        public Task<KnowledgeCatalogListResult> ListAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("壊れた");
        public Task<KnowledgeCatalogWriteResult> CreateAsync(KnowledgeDocument document, CancellationToken cancellationToken = default) => throw new InvalidOperationException("壊れた");
        public Task<KnowledgeCatalogWriteResult> PutBodyAsync(Guid documentId, string body, CancellationToken cancellationToken = default) => throw new InvalidOperationException("壊れた");
        public Task<KnowledgeCatalogWriteResult> DeleteAsync(Guid documentId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("壊れた");
    }
}

// FR-06, FR-08, #1300: 写しのポートの記録用（自動生成・作り直し・`/policy` の経路の試験が使う）。
internal sealed class RecordingDraftKnowledgeCopy : IReportDraftKnowledgeCopy
{
    public List<(TradingReport Report, int Version)> Published { get; } = [];

    public List<(ReportKind Kind, string PeriodKey)> Removed { get; } = [];

    public Task PublishAsync(TradingReport report, int version, CancellationToken cancellationToken = default)
    {
        Published.Add((report, version));
        return Task.CompletedTask;
    }

    public Task RemoveAsync(ReportKind kind, string periodKey, CancellationToken cancellationToken = default)
    {
        Removed.Add((kind, periodKey));
        return Task.CompletedTask;
    }
}
