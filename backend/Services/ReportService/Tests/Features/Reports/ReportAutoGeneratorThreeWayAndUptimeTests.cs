using ReportService.Infrastructure.Persistence;
using ReportService.Infrastructure.ExternalServices;
using ReportService.Common.Abstractions;
using ReportService.Features.Reports;
using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-15, FR-16, FR-20, #569, 04_report-templates 月報 §5 / §6.2・日報 §1, IADR-0250, IADR-0271:
// 三者比較・OpenD 稼働率の供給が**自動生成へ結線されている**ことと、**その失敗の向き**を固定する。
//
// 🔴 「呼ばれたこと」と「結果が出口へ出たこと」は別の事実である。本テストは**生成された本文**を見る。
public class ReportAutoGeneratorThreeWayAndUptimeTests
{
    // 2026-07-08（水）16:00 JST ＝ 07:00 UTC。日報だけが生成境界を越えている時刻。
    private static readonly DateTimeOffset WedAfterClose = new(2026, 7, 8, 7, 0, 0, TimeSpan.Zero);

    // 2026-07-31（金）17:00 JST ＝ 08:00 UTC。月報の生成境界を越えている時刻。
    private static readonly DateTimeOffset MonthEndAfterClose = new(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class StubDrafter : IReportNarrativeDrafter
    {
        public Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult("自動生成の散文");
    }

    private sealed class StubUptimeSource(OpenDUptimeRecord? record) : IOpenDUptimeSource
    {
        public List<(DateOnly From, DateOnly To)> Requested { get; } = [];

        public Task<OpenDUptimeRecord?> GetUptimeAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
        {
            Requested.Add((from, to));
            return Task.FromResult(record);
        }
    }

    private sealed class ThrowingUptimeSource : IOpenDUptimeSource
    {
        public Task<OpenDUptimeRecord?> GetUptimeAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("リスク管理サービスへ到達できません");
    }

    private sealed class StubStageSource(TradingStage? stage) : IStageProgressSource
    {
        public Task<TradingStage?> GetCurrentStageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(stage);
    }

    private sealed class ThrowingStageSource : IStageProgressSource
    {
        public Task<TradingStage?> GetCurrentStageAsync(CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("リスク管理サービスへ到達できません");
    }

    private sealed class StubFillSource(IReadOnlyList<PeriodTradeFill> fills) : IPeriodFillSource
    {
        public Task<IReadOnlyList<PeriodTradeFill>> GetFillsAsync(
            DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            Task.FromResult(fills);
    }

    private static ReportAutoGenerator NewGenerator(
        IReportStore store,
        DateTimeOffset now,
        IOpenDUptimeSource? uptimeSource = null,
        IStageProgressSource? stageProgressSource = null,
        IPeriodFillSource? fillSource = null) =>
        new(store,
            new ReportDraftService(new StubDrafter()),
            fillSource ?? new NoOpPeriodFillSource(),
            new FixedClock(now),
            new ReportAutoGenerationSettings(),
            uptimeSource: uptimeSource,
            stageProgressSource: stageProgressSource);

    private static string BodyOf(IReportStore store, ReportKind kind) =>
        store.List().Single(r => r.Kind == kind).Body;

    // ---- OpenD 稼働率（日報 §1・月報 §6.2） ----

    // 🔴 **否定形**: 未注入の既定は「未供給」である（稼働率 0% ではない）。
    [Fact]
    public async Task 稼働率の供給元が未注入なら未供給として描く()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, WedAfterClose).RunOnceAsync();

        BodyOf(store, ReportKind.Daily)
            .Should().Contain("OpenD 稼働率（当日の通常取引時間に対する比率） | **供給されていません**（稼働率 0% ではありません）");
    }

    // 🔴 **否定形**: 照会が失敗しても未供給へ倒す（空の記録＝0 日へ倒さない）。
    [Fact]
    public async Task 稼働率の照会が失敗しても未供給へ倒す()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, WedAfterClose, uptimeSource: new ThrowingUptimeSource()).RunOnceAsync();

        BodyOf(store, ReportKind.Daily)
            .Should().Contain("OpenD 稼働率（当日の通常取引時間に対する比率） | **供給されていません**（稼働率 0% ではありません）");
    }

    // **対の肯定形**: 供給された稼働率が日報の本文へ確かに出る（算入可否つき）。
    [Fact]
    public async Task 供給された稼働率を日報へ載せる()
    {
        var store = new InMemoryReportStore();
        var record = new OpenDUptimeRecord(
            [new OpenDUptimeDay(new DateOnly(2026, 7, 8), 0.62m)], Stage1CumulativeCountedDays: 41);

        await NewGenerator(store, WedAfterClose, uptimeSource: new StubUptimeSource(record)).RunOnceAsync();

        BodyOf(store, ReportKind.Daily)
            .Should().Contain("OpenD 稼働率（当日の通常取引時間に対する比率） | 62.0% — Stage 1 の日数算入: 算入（50% 以上）");
    }

    // **対の肯定形**: 月報 §6.2 の分布と累計算入日数が出る。
    [Fact]
    public async Task 供給された稼働率を月報の分布へ載せる()
    {
        var store = new InMemoryReportStore();
        var record = new OpenDUptimeRecord(
        [
            new OpenDUptimeDay(new DateOnly(2026, 7, 29), 1.0m),
            new OpenDUptimeDay(new DateOnly(2026, 7, 30), 0.60m),
            new OpenDUptimeDay(new DateOnly(2026, 7, 31), 0.20m),
        ], Stage1CumulativeCountedDays: 41);

        await NewGenerator(store, MonthEndAfterClose, uptimeSource: new StubUptimeSource(record)).RunOnceAsync();

        var body = BodyOf(store, ReportKind.Monthly);
        body.Should().Contain("| 100% | 1 日 |");
        body.Should().Contain("| 50〜99%（Stage 1 の日数に算入する） | 1 日 |");
        body.Should().Contain("| 50% 未満（Stage 1 の日数に算入しない） | 1 日 |");
        body.Should().Contain("- Stage 1 の累計算入日数: 41 / 60 日");
        body.Should().NotContain("**稼働率の観測を照会できませんでした（供給元がありません）**");
    }

    // 🔴 **変異試験で生き残った穴を塞ぐ（#569）**: 日報のセルは「Days が空」でも
    // 「供給されていません」と描くため、**未注入を空の記録（0 日）へ倒しても日報のテストは緑になる**。
    // 月報 §5 の分布は空の記録なら「100%: 0 日 …」を出してしまうため、ここで両者を分けて固定する。
    [Fact]
    public async Task 稼働率の未注入と観測0日を月報で描き分ける()
    {
        var unsupplied = new InMemoryReportStore();
        await NewGenerator(unsupplied, MonthEndAfterClose).RunOnceAsync();

        var thrown = new InMemoryReportStore();
        await NewGenerator(thrown, MonthEndAfterClose, uptimeSource: new ThrowingUptimeSource()).RunOnceAsync();

        var empty = new InMemoryReportStore();
        await NewGenerator(empty, MonthEndAfterClose, uptimeSource: new StubUptimeSource(new OpenDUptimeRecord([]))).RunOnceAsync();

        // 未注入・照会失敗は「照会できませんでした」。
        BodyOf(unsupplied, ReportKind.Monthly)
            .Should().Contain("**稼働率の観測を照会できませんでした（供給元がありません）**");
        BodyOf(thrown, ReportKind.Monthly)
            .Should().Contain("**稼働率の観測を照会できませんでした（供給元がありません）**");

        // 引けたが観測が 1 日も無い場合は**分布の 0 日**として描く（別の事実である）。
        BodyOf(empty, ReportKind.Monthly)
            .Should().NotContain("**稼働率の観測を照会できませんでした（供給元がありません）**");
        BodyOf(empty, ReportKind.Monthly).Should().Contain("| 100% | 0 日 |");
    }

    // 供給の照会は**当該報告書の期間**で行う（別の期間の稼働率を載せない）。
    [Fact]
    public async Task 稼働率の照会は当該報告書の期間で行う()
    {
        var source = new StubUptimeSource(new OpenDUptimeRecord([]));

        await NewGenerator(new InMemoryReportStore(), WedAfterClose, uptimeSource: source).RunOnceAsync();

        source.Requested.Should().Contain((new DateOnly(2026, 7, 8), new DateOnly(2026, 7, 8)));
    }

    // ---- 三者比較（月報 §5） ----

    // 🔴 **否定形**: 段階の供給元が未注入なら三者比較は節ごと未供給である。
    [Fact]
    public async Task 段階の供給元が未注入なら三者比較を未供給として描く()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, MonthEndAfterClose).RunOnceAsync();

        BodyOf(store, ReportKind.Monthly)
            .Should().Contain("**三者比較の実績を照会できませんでした（供給元がありません）**");
    }

    // 🔴 **否定形**: 段階の照会が失敗しても未供給へ倒す（Stage 0 へ倒さない）。
    [Fact]
    public async Task 段階の照会が失敗しても三者比較を未供給へ倒す()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, MonthEndAfterClose, stageProgressSource: new ThrowingStageSource()).RunOnceAsync();

        BodyOf(store, ReportKind.Monthly)
            .Should().Contain("**三者比較の実績を照会できませんでした（供給元がありません）**");
    }

    // **対の肯定形**: 段階と約定が供給されれば、月報 §5 に実値が出る。
    [Fact]
    public async Task 供給された段階と約定から三者比較の実値を載せる()
    {
        var store = new InMemoryReportStore();
        var t0 = new DateTimeOffset(2026, 7, 15, 14, 0, 0, TimeSpan.Zero);
        var fills = new List<PeriodTradeFill>
        {
            new("AAPL", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 10, 100m, t0,
                Guid.NewGuid(), BrokerProvider.MoomooSimulate),
            new("AAPL", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, 10, 120m, t0.AddHours(1),
                Guid.NewGuid(), BrokerProvider.MoomooSimulate),
        };

        await NewGenerator(store, MonthEndAfterClose,
            stageProgressSource: new StubStageSource(TradingStage.Stage1Simulate),
            fillSource: new StubFillSource(fills)).RunOnceAsync();

        var body = BodyOf(store, ReportKind.Monthly);
        body.Should().NotContain("**三者比較の実績を照会できませんでした（供給元がありません）**");
        // SIMULATE 列は実値、実弾列は到達していないため「該当なし」。
        body.Should().Contain("| 取引件数 | 該当なし | 2 件 | 該当なし |");
        body.Should().Contain("| 勝率 | 該当なし | 100.0% | 該当なし |");
    }

    // 🔴 **本 issue の核心が出口まで届いていること**: 到達済みで約定が無い段は「0 件」、
    // 到達していない段は「該当なし」と**書き分かれる**。
    [Fact]
    public async Task 到達済みの0件と未到達の該当なしを書き分ける()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, MonthEndAfterClose,
            stageProgressSource: new StubStageSource(TradingStage.Stage1Simulate)).RunOnceAsync();

        BodyOf(store, ReportKind.Monthly).Should().Contain("| 取引件数 | 該当なし | 0 件 | 該当なし |");
    }

    // 🔴 発注先が記録されていない約定は列へ算入せず、**件数を明記する**（黙って落とさない）。
    [Fact]
    public async Task 発注先不明の約定件数を月報へ明記する()
    {
        var store = new InMemoryReportStore();
        var t0 = new DateTimeOffset(2026, 7, 15, 14, 0, 0, TimeSpan.Zero);
        var fills = new List<PeriodTradeFill>
        {
            new("AAPL", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 10, 100m, t0),
        };

        await NewGenerator(store, MonthEndAfterClose,
            stageProgressSource: new StubStageSource(TradingStage.Stage2MinimalLive),
            fillSource: new StubFillSource(fills)).RunOnceAsync();

        BodyOf(store, ReportKind.Monthly)
            .Should().Contain("- 発注先が記録されていない約定が 1 件あり、**どの列にも算入していません**");
    }

    // 🔴 三者比較は**月報だけ**が持つ（計画の粒度対応表）。日報・週報へ漏れない。
    [Fact]
    public async Task 三者比較は日報には出さない()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, WedAfterClose,
            stageProgressSource: new StubStageSource(TradingStage.Stage2MinimalLive)).RunOnceAsync();

        BodyOf(store, ReportKind.Daily).Should().NotContain("バックテスト / SIMULATE / 実弾の三者比較");
    }
}
