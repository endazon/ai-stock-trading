using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Common.Abstractions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Infrastructure.Persistence;
using Xunit;

namespace ReportService.Tests;

// T-06-080〜T-06-083・T-06-087, FR-06, FR-16, UC-03, UC-04, 計画 ADR-0059 決定 2〜4, #1218, IADR-0519 決定 2〜5: 自動生成（日報・週報）が
// 前週の週報 §6 の書式行を参照値とし、週報 §1 と同じ定義（同じ窓の規則・同じ集計）で数えた週初来の実現損益と照合すること。
// 週の最終営業日の日報 §6 と週報 §1 の値が一致すること。書式どおりの行が無い週報は確定の前に警告すること。
public class ReportAutoGeneratorWeeklyGoalTests
{
    // 2026-10-06（火）17:00 JST ＝ daily-2026-10-06 だけが生成対象。当週は W41（10-05〜10-09）、前週は W40。
    private static readonly DateTimeOffset TueAfterBoundary = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);

    // 2026-10-09（金）17:00 JST ＝ daily-2026-10-09 と weekly-2026-W41 が生成対象（週報の境界 16:30 の後）。
    private static readonly DateTimeOffset FriAfterWeekly = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    // 米国の現地取引日 ET 2026-10-dd の 10:00（EDT・UTC−4）。
    private static DateTimeOffset Et(int day) => new(2026, 10, day, 14, 0, 0, TimeSpan.Zero);

    private const string GoalPolicy = "押し目買いを優先する。\n数値目標: -200 〜 +500 USD";

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    // 散文の文脈を記録し、日報には区切り行つきの 2 部を返す。
    private sealed class RecordingDrafter : IReportNarrativeDrafter
    {
        public List<ReportNarrativeContext> Contexts { get; } = [];

        public Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            return Task.FromResult(context.Kind == ReportKind.Daily
                ? $"市況の散文。\n{DailyNarrativeSections.Marker}\n振り返りの散文。"
                : "週の散文。");
        }
    }

    // 台帳は窓より広く返す（生成器が窓で絞る）。
    private sealed class LedgerFills(params PeriodTradeFill[] fills) : IPeriodFillSource
    {
        public Task<IReadOnlyList<PeriodTradeFill>> GetFillsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PeriodTradeFill>>(fills);
    }

    private sealed class NoDrift : IPeriodDriftAdoptionSource
    {
        public Task<IReadOnlyList<PeriodDriftAdoption>?> GetDriftAdoptionsAsync(
            DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PeriodDriftAdoption>?>([]);
    }

    private sealed class NoPositions : IOpenPositionSource
    {
        public Task<IReadOnlyList<ReportPosition>?> GetOpenPositionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ReportPosition>?>([]);
    }

    private sealed class Opening(params OpeningLot[] lots) : IOpeningInventorySource
    {
        public Task<IReadOnlyList<OpeningLot>?> GetOpeningInventoryAsync(
            Market market, DateOnly beforeTradingDay, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OpeningLot>?>([.. lots.Where(l => l.Market == market)]);
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

    private static ReportAutoGenerator Generator(
        IReportStore store, DateTimeOffset now, IPeriodFillSource fills, IReportNarrativeDrafter drafter,
        IOpeningInventorySource? opening = null, IReportDraftPresentedNotifier? notifier = null) =>
        new(store, new ReportDraftService(drafter), fills, new FixedClock(now), new ReportAutoGenerationSettings(), notifier,
            openPositionSource: new NoPositions(), driftAdoptionSource: new NoDrift(), openingInventorySource: opening);

    private static void SeedWeekly(InMemoryReportStore store, string key, DateOnly start, string policy, bool confirm)
    {
        var version = store.UpsertDraft(new TradingReport { PeriodKey = key, Kind = ReportKind.Weekly, PeriodStart = start, PolicySummary = policy }, 0);
        if (confirm)
            store.Confirm(key, version, DateTimeOffset.UnixEpoch);
    }

    private static string Section(string md, string heading)
    {
        var start = md.IndexOf(heading, StringComparison.Ordinal) + heading.Length;
        var next = md.IndexOf("\n## ", start, StringComparison.Ordinal);
        return next < 0 ? md[start..] : md[start..next];
    }

    private const string DailyReview = "## 6. 振り返り（週次目標との照合）";

    // ---- T-06-081・T-06-082: 火曜の日報は月〜火の窓（前週金曜の米国セッションを含む）で週初来を数え、参照値と照合する ----

    [Fact]
    public async Task 火曜の日報は週初来の窓で数えた実現損益を前週の週報の目標と照合する()
    {
        var store = new InMemoryReportStore();
        SeedWeekly(store, "weekly-2026-W40", new DateOnly(2026, 9, 28), GoalPolicy, confirm: true);
        PeriodTradeFill[] fills =
        [
            // ET 10-02（金）の米国セッション: 10-03 05:00 JST に閉場 → 前週の週報の窓の外・当週（W41）の週初来の窓の中。
            new("AAPL", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 10, 100m, Et(2)),
            // ET 10-05 のセッション: daily-2026-10-06 の窓の中。
            new("AAPL", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, 10, 130m, Et(5)),
            // ET 10-06 のセッション: 10-07 05:00 JST に閉場 → 火曜の日報の週初来に入らない。
            new("MSFT", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 5, 400m, Et(6)),
            new("MSFT", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, 5, 300m, Et(6).AddHours(1)),
        ];
        var drafter = new RecordingDrafter();

        await Generator(store, TueAfterBoundary, new LedgerFills(fills), drafter).RunOnceAsync();

        // 期待値は集計の部品から独立に計算する（週初来＝ET 10-02 と 10-05 の 2 約定）。
        var expected = PnlAggregator.Aggregate(fills[..2], TradingAssumptionsDefaults.Create(), null, []).RealizedPnlNet;
        expected.Should().BeInRange(0m, 500m, "前提: 範囲内に収まる値を作っている");
        var body = Section(store.Get("daily-2026-10-06")!.Report.Body, DailyReview);
        body.Should().Contain($"- 週次目標: -200.00 USD 〜 +500.00 USD（weekly-2026-W40 の §6 の数値目標）に対し、"
            + $"週初来の実現損益（税引後・費用込み）は {ReportAmountFormat.Base(expected)} で **範囲内**です。");
        body.Should().Contain("振り返りの散文。").And.NotContain("市況の散文。");
        Section(store.Get("daily-2026-10-06")!.Report.Body, "## 5. 市況・特記事項").Should().Contain("市況の散文。").And.NotContain("振り返りの散文。");

        // 日報自身の窓（ET 10-05 だけ）では買いが無く算定できない＝週初来は日報の窓の値ではない。
        store.Get("daily-2026-10-06")!.Report.Body.Should().Contain("| 実現損益（税引後・費用込み） | **算出不能**");

        // 散文へは照合の事実（コードの値）を渡す（T-06-088）。
        var context = drafter.Contexts.Single();
        context.WeeklyGoal!.Outcome.Should().Be(WeeklyGoalOutcome.Compared);
        context.WeeklyGoal.Actual.Amount.Should().Be(expected);
        ReportNarrativePromptBuilder.Build(context).Should().Contain(DailyNarrativeSections.Marker)
            .And.Contain($"週初来の実現損益(税引後・費用込み)は {ReportAmountFormat.Base(expected)} で 範囲内")
            .And.Contain(ReportNarrativePromptBuilder.WeeklyGoalRule);
    }

    // ---- T-06-082: 週の最終営業日の日報 §6 の値と週報 §1 の値が一致する（持ち越しの在庫を含む） ----

    [Fact]
    public async Task 金曜の日報の週初来の値は週報の週間実現損益と一致する()
    {
        var store = new InMemoryReportStore();
        SeedWeekly(store, "weekly-2026-W40", new DateOnly(2026, 9, 28), GoalPolicy, confirm: true);
        OpeningLot[] carried = [new("NVDA", Market.UnitedStates, 10, 100m, 150m, 0)];
        PeriodTradeFill[] fills =
        [
            new("AAPL", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 10, 100m, Et(2)),
            new("AAPL", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, 10, 140m, Et(5)),
            new("NVDA", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, 10, 160m, Et(7)),
            new("MSFT", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 5, 200m, Et(6)),
            new("MSFT", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, 5, 190m, Et(8)),
        ];

        await Generator(store, FriAfterWeekly, new LedgerFills(fills), new RecordingDrafter(), new Opening(carried)).RunOnceAsync();

        var expected = PnlAggregator.Aggregate(
            fills, TradingAssumptionsDefaults.Create(), null, [], new OpeningInventorySnapshot(carried)).RealizedPnlNet;
        var amount = ReportAmountFormat.Base(expected);
        expected.Should().BeGreaterThan(500m, "前提: 上限を上回る値を作っている");
        store.Get("weekly-2026-W41")!.Report.Body.Should().Contain($"| 週間実現損益（税引後・費用込み） | {amount} |");
        Section(store.Get("daily-2026-10-09")!.Report.Body, DailyReview)
            .Should().Contain($"週初来の実現損益（税引後・費用込み）は {amount} で **上限を上回る（上限との差 {ReportAmountFormat.Base(expected - 500m)}）**です。");

        // T-06-087: 週報 §1 は同じ参照値（W40）と §1 の値で照合し、達成・未達を書かない。§4 は散文の前に照合の行。
        var weekly = store.Get("weekly-2026-W41")!.Report.Body;
        weekly.Should().Contain("| 週次目標に対する達成 | **判定保留**").And.Contain($"の **上限を上回る（上限との差 {ReportAmountFormat.Base(expected - 500m)}）**（詳細は §4） |");
        Section(weekly, "## 4. 振り返りと評価").Should().Contain($"週間実現損益（税引後・費用込み）は {amount} で **上限を上回る");
    }

    // ---- T-06-083: 前週の週報が未確定なら最新の確定済みを注記つきで・当週以後は使わない・確定済みが無ければ週次目標なし ----

    [Fact]
    public async Task 前週の週報が未確定なら最新の確定済み週報の目標と注記つきで照らす()
    {
        var store = new InMemoryReportStore();
        SeedWeekly(store, "weekly-2026-W39", new DateOnly(2026, 9, 21), "数値目標: 0 〜 +100 USD", confirm: true);
        SeedWeekly(store, "weekly-2026-W40", new DateOnly(2026, 9, 28), GoalPolicy, confirm: false);

        await Generator(store, TueAfterBoundary, new LedgerFills(), new RecordingDrafter()).RunOnceAsync();

        Section(store.Get("daily-2026-10-06")!.Report.Body, DailyReview)
            .Should().Contain("0.00 USD 〜 +100.00 USD（weekly-2026-W39 の §6 の数値目標）")
            .And.Contain("- 注記: 前週の週報（weekly-2026-W40）が確定していないため、最新の確定済み週報 weekly-2026-W39 の目標と照らしています。");
    }

    [Fact]
    public async Task 当週以後の確定済み週報は参照値にせず_確定済みが無ければ週次目標なしと書く()
    {
        var store = new InMemoryReportStore();
        // 当週（W41）の週報は「翌週（W42）の目標」を持つ。日報の上位方針（最新の確定済み）とは違い、照合には使わない。
        SeedWeekly(store, "weekly-2026-W41", new DateOnly(2026, 10, 5), GoalPolicy, confirm: true);

        await Generator(store, TueAfterBoundary, new LedgerFills(), new RecordingDrafter()).RunOnceAsync();

        Section(store.Get("daily-2026-10-06")!.Report.Body, DailyReview)
            .Should().Contain("**週次目標なし**（確定済みの週報がありません）").And.NotContain("weekly-2026-W41");
    }

    // ---- T-06-084: 参照する週報の方針が書式どおりでなければ照合不能 ----

    [Fact]
    public async Task 前週の週報の方針が書式どおりでなければ照合不能と書く()
    {
        var store = new InMemoryReportStore();
        SeedWeekly(store, "weekly-2026-W40", new DateOnly(2026, 9, 28), "翌週は +500 USD 程度の利益を目指す。", confirm: true);

        await Generator(store, TueAfterBoundary, new LedgerFills(), new RecordingDrafter()).RunOnceAsync();

        Section(store.Get("daily-2026-10-06")!.Report.Body, DailyReview)
            .Should().Contain("**照合不能**（週次目標が書式どおりでない: weekly-2026-W40 の方針に「数値目標:」行がありません）");
    }

    // ---- T-06-080: 週報の初稿の方針に書式どおりの行が無ければ提示の要約で警告する（確定は止めない） ----

    [Theory]
    [InlineData("押し目買いを優先する。", true)]
    [InlineData(GoalPolicy, false)]
    public async Task 週報の初稿の方針に書式どおりの数値目標の行が無ければ提示の要約で警告する(string previousPolicy, bool warns)
    {
        var store = new InMemoryReportStore();
        SeedWeekly(store, "weekly-2026-W40", new DateOnly(2026, 9, 28), previousPolicy, confirm: true);
        var notifier = new RecordingNotifier();

        await Generator(store, FriAfterWeekly, new LedgerFills(), new RecordingDrafter(), notifier: notifier).RunOnceAsync();

        var weekly = notifier.Notices.Single(n => n.PeriodKey == "weekly-2026-W41");
        weekly.Summary.Contains(ReportSummaryMarkers.WeeklyGoalLineMissingPrefix, StringComparison.Ordinal).Should().Be(warns);
        notifier.Notices.Single(n => n.PeriodKey == "daily-2026-10-09").Summary
            .Should().NotContain(ReportSummaryMarkers.WeeklyGoalLineMissingPrefix, "日報の方針には出さない");
        store.Get("weekly-2026-W41")!.Report.PolicySummary.Should().NotContain(ReportSummaryMarkers.WeeklyGoalLineMissingPrefix, "方針の本文へ入れない");
        store.GetReview("weekly-2026-W41")!.State.Should().Be(ReviewState.PendingApproval, "確定は止めない（承認待ちまで進む）");
    }

    // /policy の週報の改訂案に書式どおりの行が無ければ、案内文と本文の改訂の記録で警告する（確定・提示は止めない）。
    [Fact]
    public async Task 週報の改訂案に書式どおりの数値目標の行が無ければ案内文と本文の記録で警告する()
    {
        var store = new InMemoryReportStore();
        SeedWeekly(store, "weekly-2026-W41", new DateOnly(2026, 10, 5), "旧方針", confirm: false);
        store.ApplyReview("weekly-2026-W41", new ReviewCommand(ReviewAction.Present, "scheduler", 1));
        var service = new ReportPolicyRevisionService(
            store, new FixedClock(FriAfterWeekly), new FakeReviser("押し目買いを優先する。"),
            new PolicyRevisionSchedule(new ReportScheduleOptions(), AutoDailyEnabled: true),
            new InMemoryPolicyRevisionLedger(), new PolicyRevisionLimit(10),
            NullLogger<ReportPolicyRevisionService>.Instance);

        var result = await service.ReviseAsync("weekly-2026-W41", "目標を決めたい", "developer");

        (result.Status, result.Presented).Should().Be((PolicyRevisionStatus.Proposed, true));
        var warning = WeeklyGoalLineCheck.WarningFor(ReportKind.Weekly, "押し目買いを優先する。")!;
        result.Message.Split('\n').Should().Contain(warning);
        var saved = store.Get("weekly-2026-W41")!.Report;
        saved.PolicySummary.Should().Be("押し目買いを優先する。");
        saved.Body.IndexOf(warning, StringComparison.Ordinal).Should().BeGreaterThan(0)
            .And.BeLessThan(saved.Body.IndexOf("### 改訂後の方針（AI の案）", StringComparison.Ordinal));
    }

    private sealed class FakeReviser(string policy) : IReportPolicyReviser
    {
        public Task<PolicyRevisionOutcome> ReviseAsync(PolicyRevisionContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(PolicyRevisionOutcome.Proposed(new PolicyRevisionProposal(policy, [], "説明"), null));
    }
}
