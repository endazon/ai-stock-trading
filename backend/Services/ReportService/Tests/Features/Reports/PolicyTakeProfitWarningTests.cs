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
// 日報の方針の利確条件を数値で書くよう方針の改訂 LLM へ求め、数値の利確条件が無い方針は**確定の前に警告する**（確定は止めない）。
// 警告は方針の本文へ入れない（方針はそのまま判断へ渡る）。T-10-1884〜1887。
public class PolicyTakeProfitWarningTests
{
    private const string VaguePolicy = "含み益が十分に出た段階で利確する。押し目買いを優先する。";
    private const string NumericPolicy = "AAPL: 取得単価から +5% で利確。押し目買いを優先する。";

    // ---- T-10-1884: 方針の改訂のプロンプト ----

    [Fact]
    public void 日報の改訂のプロンプトは利確の条件を数値で書くよう求め_数値の無い語を退ける()
    {
        var prompt = PolicyRevisionPromptBuilder.Build(
            new PolicyRevisionContext(ReportKind.Daily, "daily-2026-10-01", VaguePolicy, null, "利確を早めたい"));

        prompt.Should().Contain(PolicyRevisionPromptBuilder.NumericTakeProfitHeading)
            .And.Contain(PolicyRevisionPromptBuilder.NumericTakeProfitRule)
            .And.Contain(PolicyRevisionPromptBuilder.VagueTakeProfitRule);
        PolicyRevisionPromptBuilder.VagueTakeProfitRule.Should().Contain("十分に").And.Contain("適切に");
        PolicyRevisionPromptBuilder.NumericTakeProfitRule.Should().Contain("取得単価").And.Contain("%").And.Contain("割合");

        // 案内はデータ（利用者の指示）より前に置く（指示で案内を偽装できない。材料の節と同じ構造分離）。
        prompt.IndexOf(PolicyRevisionPromptBuilder.NumericTakeProfitHeading, StringComparison.Ordinal)
            .Should().BeLessThan(prompt.IndexOf("ownerInstruction:", StringComparison.Ordinal));
        // 材料の節（#1118）は変えない。
        prompt.Should().Contain(PolicyRevisionPromptBuilder.DecisionMaterialsHeading);
    }

    // 案内の例は、確定の前の警告と判断側が使う同じ部品で数値の利確条件として読める（例が警告に掛からない）。
    [Fact]
    public void 案内の例は数値の利確条件として読める()
    {
        AiStockTrading.Shared.Kernel.Trading.PolicyTakeProfitConditions
            .HasAny(PolicyRevisionPromptBuilder.NumericTakeProfitRule).Should().BeTrue();
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
    public void 日報の方針に数値の利確条件が無ければ警告し_あれば警告しない()
    {
        PolicyTakeProfitCheck.WarningFor(ReportKind.Daily, VaguePolicy).Should().Be(PolicyTakeProfitCheck.Warning);
        PolicyTakeProfitCheck.WarningFor(ReportKind.Daily, null).Should().Be(PolicyTakeProfitCheck.Warning);
        PolicyTakeProfitCheck.WarningFor(ReportKind.Daily, NumericPolicy).Should().BeNull();
        PolicyTakeProfitCheck.Warning.Should().StartWith(ReportSummaryMarkers.PolicyTakeProfitMissingPrefix)
            .And.Contain("確定はできます");
    }

    [Theory]
    [InlineData(ReportKind.Weekly)]
    [InlineData(ReportKind.Monthly)]
    public void 週報と月報の方針は警告しない(ReportKind kind)
    {
        PolicyTakeProfitCheck.WarningFor(kind, VaguePolicy).Should().BeNull();
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
    public async Task 数値の利確条件が無い案は保存し承認待ちにしたうえで案内文と本文の記録で警告する()
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
    public async Task 数値の利確条件がある案は警告しない()
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
    public async Task 日報の初稿の方針に数値の利確条件が無ければ提示の要約で警告する(string previousPolicy, bool warns)
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
