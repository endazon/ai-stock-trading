using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Common.Abstractions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Infrastructure.ExternalServices;
using ReportService.Infrastructure.Persistence;
using Xunit;

namespace ReportService.Tests;

// FR-04, FR-07, ADR-0003, ADR-0048 決定 4, #1129, IADR-0470 決定 1・4（オーナー裁定 2026-10-01）:
// 日報の方針の利確条件を決まった書式の「利確:」行で書くよう方針の改訂 LLM へ求め、書式どおりの行が無い（または書式に合わない
// 「利確:」行がある）方針は**確定の前に警告する**（確定は止めない。IADR-0470 の 2026-10-01 追記 / #1129 再監査）。
// 警告は方針の本文へ入れない（方針はそのまま判断へ渡る）。T-10-1884〜1887・T-10-1946。
public class PolicyTakeProfitWarningTests
{
    private const string VaguePolicy = "含み益が十分に出た段階で利確する。押し目買いを優先する。";
    private const string NumericPolicy = "AAPL は取得単価から 5% 上がったら利確する。押し目買いを優先する。\n利確: AAPL +5%";

    // ---- T-10-1884: 方針の改訂のプロンプト ----

    [Fact]
    public void 日報の改訂のプロンプトは利確の条件を数値で書くよう求め_数値の無い語を退ける()
    {
        var prompt = PolicyRevisionPromptBuilder.Build(
            new PolicyRevisionContext(ReportKind.Daily, "daily-2026-10-01", VaguePolicy, null, "利確を早めたい"));

        prompt.Should().Contain(PolicyRevisionPromptBuilder.NumericTakeProfitHeading)
            .And.Contain(PolicyRevisionPromptBuilder.NumericTakeProfitRule)
            .And.Contain(PolicyRevisionPromptBuilder.TakeProfitLineExamplesRule)
            .And.Contain(PolicyRevisionPromptBuilder.TakeProfitLineStrictRule)
            .And.Contain(PolicyRevisionPromptBuilder.VagueTakeProfitRule);
        PolicyRevisionPromptBuilder.VagueTakeProfitRule.Should().Contain("十分に").And.Contain("適切に");
        PolicyRevisionPromptBuilder.NumericTakeProfitRule.Should().Contain("利確: <ティッカー> <しきい値>").And.Contain("+N%")
            .And.Contain("全銘柄").And.Contain("(N%)").And.Contain("\\n");
        PolicyRevisionPromptBuilder.TakeProfitLineStrictRule.Should().Contain("説明の文");
        PolicyRevisionPromptBuilder.TakeProfitLineExamples.Should().HaveCount(3);

        // 案内はデータ（利用者の指示）より前に置く（指示で案内を偽装できない。材料の節と同じ構造分離）。
        prompt.IndexOf(PolicyRevisionPromptBuilder.NumericTakeProfitHeading, StringComparison.Ordinal)
            .Should().BeLessThan(prompt.IndexOf("ownerInstruction:", StringComparison.Ordinal));
        // 材料の節（#1118）は変えない。
        prompt.Should().Contain(PolicyRevisionPromptBuilder.DecisionMaterialsHeading);
    }

    // 案内の例は、確定の前の警告と判断側が使う同じ部品で、案内の説明どおりの条件として読める（例が警告に掛からない）。
    [Fact]
    public void 案内の例は説明どおりの条件として読める()
    {
        var conditions = AiStockTrading.Shared.Kernel.Trading.PolicyTakeProfitConditions
            .Parse(string.Join('\n', PolicyRevisionPromptBuilder.TakeProfitLineExamples));

        conditions.Select(c => (c.Symbol, c.Kind, c.Threshold, c.PartialPercent)).Should().Equal(
            ("AAPL", AiStockTrading.Shared.Kernel.Trading.TakeProfitThresholdKind.GainPercent, 5m, (decimal?)null),
            ("MSFT", AiStockTrading.Shared.Kernel.Trading.TakeProfitThresholdKind.Price, 450m, (decimal?)50m),
            ((string?)null, AiStockTrading.Shared.Kernel.Trading.TakeProfitThresholdKind.GainPercent, 8m, (decimal?)null));
        PolicyTakeProfitCheck.WarningFor(ReportKind.Daily, string.Join('\n', PolicyRevisionPromptBuilder.TakeProfitLineExamples))
            .Should().BeNull();
    }

    [Theory]
    [InlineData(ReportKind.Weekly)]
    [InlineData(ReportKind.Monthly)]
    public void 週報と月報の改訂のプロンプトには利確の案内を出さない(ReportKind kind)
    {
        var prompt = PolicyRevisionPromptBuilder.Build(
            new PolicyRevisionContext(kind, "weekly-2026-W40", VaguePolicy, null, "指示"));

        prompt.Should().NotContain(PolicyRevisionPromptBuilder.NumericTakeProfitHeading);
    }

    // ---- T-10-1885: 警告の判定 ----

    [Fact]
    public void 日報の方針に書式どおりの利確の行が無ければ警告し_あれば警告しない()
    {
        PolicyTakeProfitCheck.WarningFor(ReportKind.Daily, VaguePolicy).Should().Be(PolicyTakeProfitCheck.Warning);
        PolicyTakeProfitCheck.WarningFor(ReportKind.Daily, null).Should().Be(PolicyTakeProfitCheck.Warning);
        PolicyTakeProfitCheck.WarningFor(ReportKind.Daily, NumericPolicy).Should().BeNull();
        PolicyTakeProfitCheck.Warning.Should().StartWith(ReportSummaryMarkers.PolicyTakeProfitMissingPrefix)
            .And.Contain("確定はできます")
            .And.Contain("利確: AAPL +5%", "直し方（行の書式の例）まで書く")
            .And.Contain("利確: 全銘柄");
    }

    // 🔴 自由文の数値は読まない（書式どおりの行が無ければ数値があっても警告する）。書式に合わない「利確:」行があれば、
    // 書式どおりの行があっても警告する（方針全体の利確の条件が読まれないため）。
    [Theory]
    [InlineData("AAPLは+5%で利確、MSFTは230ドルで利確する。")]
    [InlineData("利確 AAPL +5%")]
    [InlineData("利確: AAPL +5% では利確しない")]
    [InlineData("利確: AAPL +5%\n利確: MSFT +8% 以外")]
    public void 自由文の数値と書式に合わない行は警告する(string policy)
    {
        PolicyTakeProfitCheck.WarningFor(ReportKind.Daily, policy).Should().Be(PolicyTakeProfitCheck.Warning);
    }

    [Theory]
    [InlineData(ReportKind.Weekly)]
    [InlineData(ReportKind.Monthly)]
    public void 週報と月報の方針は警告しない(ReportKind kind)
    {
        PolicyTakeProfitCheck.WarningFor(kind, VaguePolicy).Should().BeNull();
    }

    // ---- T-10-1946: 例外は銘柄の行で・数字のコードは全銘柄の行で・「利確」とコロンを含む行の扱いを案内と警告に書く（#1129 第 3 回監査 F1〜F3） ----

    [Fact]
    public void 改訂の案内と警告は例外の書き方と数字のコードの扱いとコロンのある利確の行の扱いを書く()
    {
        var prompt = PolicyRevisionPromptBuilder.Build(
            new PolicyRevisionContext(ReportKind.Daily, "daily-2026-10-01", VaguePolicy, null, "利確を早めたい"));

        // F2: 銘柄ごとの例外は、その銘柄の「利確:」行で書く（説明の文の例外は読まれない）。
        prompt.Should().Contain(PolicyRevisionPromptBuilder.TakeProfitExceptionRule);
        prompt.IndexOf(PolicyRevisionPromptBuilder.TakeProfitExceptionRule, StringComparison.Ordinal)
            .Should().BeLessThan(prompt.IndexOf("ownerInstruction:", StringComparison.Ordinal));
        PolicyRevisionPromptBuilder.TakeProfitExceptionRule.Should().Contain("その銘柄の「利確:」行").And.Contain("読まれず");
        // F3: 銘柄の行は英字のティッカーだけ。数字のコードの銘柄は「全銘柄」の行で扱う。
        PolicyRevisionPromptBuilder.NumericTakeProfitRule.Should().Contain("英字").And.Contain("「全銘柄」の行で扱う");
        // F1: 「利確」の後ろにコロンがある行はすべて「利確:」行とみなす。
        PolicyRevisionPromptBuilder.TakeProfitLineStrictRule.Should().Contain("「利確」の後ろにコロン")
            .And.Contain("利確条件:").And.Contain("AAPL 利確:");
        PolicyTakeProfitCheck.Warning.Should().Contain("英字のティッカーだけ").And.Contain("「全銘柄」の行で扱って")
            .And.Contain("「利確」の後ろにコロンがある行").And.Contain("説明の文の例外は読まれません");

        // 案内どおりの書き方は警告されず、案内が退けた書き方は警告される（警告・判断と同じ部品で判定する）。
        PolicyTakeProfitCheck.WarningFor(ReportKind.Daily, "利確: 全銘柄 +5%\n利確: AAPL +20%").Should().BeNull();
        PolicyTakeProfitCheck.WarningFor(ReportKind.Daily, "利確: 全銘柄 +5%\n利確: 7203 +8%").Should().Be(PolicyTakeProfitCheck.Warning);
        PolicyTakeProfitCheck.WarningFor(ReportKind.Daily, "利確: 全銘柄 +5%\n**利確:** AAPL +20%").Should().Be(PolicyTakeProfitCheck.Warning);
        PolicyTakeProfitCheck.WarningFor(ReportKind.Daily, "利確: 全銘柄 +5%\n利確条件: AAPL +20%").Should().Be(PolicyTakeProfitCheck.Warning);
    }

    // ---- T-10-1886: /policy の改訂案 ----

    private static readonly DateTimeOffset SundayMorning = new(2026, 9, 27, 1, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class FakeReviser(string policy) : IReportPolicyReviser
    {
        public Task<PolicyRevisionOutcome> ReviseAsync(PolicyRevisionContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(PolicyRevisionOutcome.Proposed(new PolicyRevisionProposal(policy, [], "説明"), null));
    }

    private static (ReportPolicyRevisionService Service, InMemoryReportStore Store) CreateRevision(string proposedPolicy)
    {
        var store = new InMemoryReportStore();
        var service = new ReportPolicyRevisionService(
            store, new FixedClock(SundayMorning), new FakeReviser(proposedPolicy),
            new PolicyRevisionSchedule(new ReportScheduleOptions(), AutoDailyEnabled: true),
            new InMemoryPolicyRevisionLedger(), new PolicyRevisionLimit(10),
            NullLogger<ReportPolicyRevisionService>.Instance);
        store.UpsertDraft(new TradingReport
        {
            PeriodKey = "daily-2026-09-25",
            Kind = ReportKind.Daily,
            PeriodStart = new DateOnly(2026, 9, 25),
            PolicySummary = "自動生成の方針",
            Body = "# 日報",
        }, 0);
        store.ApplyReview("daily-2026-09-25", new ReviewCommand(ReviewAction.Present, "scheduler", 1));
        return (service, store);
    }

    [Fact]
    public async Task 書式どおりの利確の行が無い案は保存し承認待ちにしたうえで案内文と本文の記録で警告する()
    {
        var (service, store) = CreateRevision(VaguePolicy);

        var result = await service.ReviseAsync("daily-2026-09-25", "利確を早めたい", "developer");

        (result.Status, result.Presented).Should().Be((PolicyRevisionStatus.Proposed, true), "警告は確定も提示も止めない");
        result.Message.Split('\n').Should().Contain(PolicyTakeProfitCheck.Warning, "案内文の 1 行として警告する（通知サービスが印で拾う）");

        var saved = store.Get("daily-2026-09-25")!.Report;
        saved.PolicySummary.Should().Be(VaguePolicy, "警告を方針の本文へ入れない（方針はそのまま判断へ渡る）");
        var warningAt = saved.Body.IndexOf(PolicyTakeProfitCheck.Warning, StringComparison.Ordinal);
        warningAt.Should().BeGreaterThan(0);
        warningAt.Should().BeLessThan(saved.Body.IndexOf("### 改訂後の方針（AI の案）", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 書式どおりの利確の行がある案は警告しない()
    {
        var (service, store) = CreateRevision(NumericPolicy);

        var result = await service.ReviseAsync("daily-2026-09-25", "利確を数値で", "developer");

        result.Status.Should().Be(PolicyRevisionStatus.Proposed);
        result.Message.Should().NotContain(ReportSummaryMarkers.PolicyTakeProfitMissingPrefix);
        store.Get("daily-2026-09-25")!.Report.Body.Should().NotContain(ReportSummaryMarkers.PolicyTakeProfitMissingPrefix);
    }

    // ---- T-10-1887: 自動生成の日報の初稿（提示の要約） ----

    private static readonly PnlSummary ZeroPnl =
        PnlAggregator.Aggregate([], AiStockTrading.Shared.Kernel.Trading.TradingAssumptionsDefaults.Create(), null);

    [Fact]
    public void 要約の警告は数値行の後_散文の前に置き_無ければ要約は変わらない()
    {
        var summary = ReportSummary.Build(ReportKind.Daily, "2026-10-01", ZeroPnl, "所感", null, PolicyTakeProfitCheck.Warning);

        var lines = summary.Split('\n');
        lines.Should().Contain(PolicyTakeProfitCheck.Warning);
        summary.IndexOf(PolicyTakeProfitCheck.Warning, StringComparison.Ordinal)
            .Should().BeLessThan(summary.IndexOf("所感", StringComparison.Ordinal));
        ReportSummary.Build(ReportKind.Daily, "2026-10-01", ZeroPnl, "所感", null, null)
            .Should().Be(ReportSummary.Build(ReportKind.Daily, "2026-10-01", ZeroPnl, "所感"));
    }

    // 2026-07-08（水）16:00 JST。日報だけが生成境界を越えている時刻。
    private static readonly DateTimeOffset WedAfterClose = new(2026, 7, 8, 7, 0, 0, TimeSpan.Zero);

    private sealed class StubDrafter : IReportNarrativeDrafter
    {
        public Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult("自動生成の散文");
    }

    private sealed class RecordingNotifier : IReportDraftPresentedNotifier
    {
        public List<PresentedReportNotice> Notices { get; } = [];

        public Task NotifyAsync(PresentedReportNotice notice, CancellationToken cancellationToken = default)
        {
            Notices.Add(notice);
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(VaguePolicy, true)]
    [InlineData(NumericPolicy, false)]
    public async Task 日報の初稿の方針に書式どおりの利確の行が無ければ提示の要約で警告する(string previousPolicy, bool warns)
    {
        var store = new InMemoryReportStore();
        var version = store.UpsertDraft(new TradingReport
        {
            PeriodKey = "daily-2026-07-07",
            Kind = ReportKind.Daily,
            PeriodStart = new DateOnly(2026, 7, 7),
            PolicySummary = previousPolicy,
        }, 0);
        store.Confirm("daily-2026-07-07", version, DateTimeOffset.UnixEpoch);
        var notifier = new RecordingNotifier();
        var generator = new ReportAutoGenerator(
            store, new ReportDraftService(new StubDrafter()), new NoOpPeriodFillSource(), new FixedClock(WedAfterClose),
            new ReportAutoGenerationSettings(), notifier);

        await generator.RunOnceAsync();

        var notice = notifier.Notices.Should().ContainSingle().Which;
        notice.PeriodKey.Should().Be("daily-2026-07-08");
        notice.Summary.Contains(PolicyTakeProfitCheck.Warning, StringComparison.Ordinal).Should().Be(warns);
        // 方針の本文は継続のまま（警告を混ぜない）。確定は止めない（承認待ち）。
        var saved = store.Get("daily-2026-07-08")!;
        saved.Report.PolicySummary.Should().NotContain(ReportSummaryMarkers.PolicyTakeProfitMissingPrefix);
        store.GetReview("daily-2026-07-08")!.State.Should().Be(ReviewState.PendingApproval);
    }
}
