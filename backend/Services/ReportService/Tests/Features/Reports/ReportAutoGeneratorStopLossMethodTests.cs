using ReportService.Infrastructure.Persistence;
using ReportService.Infrastructure.ExternalServices;
using ReportService.Common.Abstractions;
using ReportService.Features.Reports;
using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// T-10-998, FR-06, FR-10, ADR-0040 決定1, #823, IADR-0422 決定3, IADR-0352 決定5:
// 日報 §4「損切りの実行機構（当日）」の供給が自動生成へ結線されていることと、**その失敗の向き**（未供給へ倒し、
// 未供給の入力として記録する）を固定する。
//
// 🔴 **既定（未注入）が「承認なし」ではなく未供給であること**が要点である。
public class ReportAutoGeneratorStopLossMethodTests
{
    // 2026-07-08（水）16:00 JST ＝ 07:00 UTC。日報だけが生成境界を越えている時刻。
    private static readonly DateTimeOffset WedAfterClose = new(2026, 7, 8, 7, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T0 = new(2026, 7, 8, 3, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class StubDrafter : IReportNarrativeDrafter
    {
        public Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult("自動生成の散文");
    }

    private sealed class StubUsageSource(StopLossMethodUsage? usage) : IStopLossMethodUsageSource
    {
        public List<(DateOnly From, DateOnly To)> Requested { get; } = [];

        public Task<StopLossMethodUsage?> GetUsageAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
        {
            Requested.Add((from, to));
            return Task.FromResult(usage);
        }
    }

    private sealed class ThrowingUsageSource : IStopLossMethodUsageSource
    {
        public Task<StopLossMethodUsage?> GetUsageAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("監査台帳へ到達できません");
    }

    private static ReportAutoGenerator NewGenerator(IReportStore store, IStopLossMethodUsageSource? source) =>
        new(store,
            new ReportDraftService(new StubDrafter()),
            new NoOpPeriodFillSource(),
            new FixedClock(WedAfterClose),
            new ReportAutoGenerationSettings(),
            stopLossMethodUsageSource: source);

    private static TradingReport DailyOf(IReportStore store) =>
        store.List().Single(r => r.Kind == ReportKind.Daily);

    private static OrderApproved Approved(StopLossExecutionMethod method) => new(
        Guid.NewGuid(),
        new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 200m),
        10,
        T0,
        StopLossMethod: method);

    // 🔴 否定形: 未注入・照会の失敗・null はいずれも「照会できませんでした」と書き、未供給の入力として記録する。
    [Fact]
    public async Task 未注入なら未供給として描き_未供給の入力に記録する()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, null).RunOnceAsync();

        var daily = DailyOf(store);
        daily.Body.Should().Contain("- **承認の記録を照会できませんでした（要確認）**: 「承認なし」とは区別しています。");
        daily.UnsuppliedInputs.Should().Contain(ReportInput.StopLossMethods);
    }

    [Fact]
    public async Task 照会が失敗しても未供給へ倒す()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, new ThrowingUsageSource()).RunOnceAsync();

        var daily = DailyOf(store);
        daily.Body.Should().Contain("**承認の記録を照会できませんでした（要確認）**");
        daily.UnsuppliedInputs.Should().Contain(ReportInput.StopLossMethods);
    }

    // 対の肯定形: 供給されたら日報へ確かに載り、未供給の入力に数えない。照会は当該日報の期間で行う。
    [Fact]
    public async Task 供給された承認時点の手法を日報へ載せ_当日の期間で照会する()
    {
        var store = new InMemoryReportStore();
        var source = new StubUsageSource(StopLossMethodUsage.From(
            [Approved(StopLossExecutionMethod.BrokerStopOrder), Approved(StopLossExecutionMethod.NoProtectiveStop)]));

        await NewGenerator(store, source).RunOnceAsync();

        var daily = DailyOf(store);
        daily.Body.Should().Contain(
            "- **選ばれていた手法（承認時点）**: 計 2 件 — S0 ブローカー側逆指値 1 件 / S2 逆指値なしの建玉を許容 1 件");
        daily.UnsuppliedInputs.Should().NotContain(ReportInput.StopLossMethods);
        source.Requested.Should().Contain((new DateOnly(2026, 7, 8), new DateOnly(2026, 7, 8)));
    }

    // ---- T-10-1091, FR-06, FR-10, #1002, IADR-0429 決定4: 発注執行の解決結果の供給 ------------------------------

    // 2026-07-31（金）17:00 JST。日報・週報・月報のすべてが生成境界を越えている時刻。
    private static readonly DateTimeOffset MonthEndAfterClose = new(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);

    private sealed class StubResolutionSource(StopLossMethodResolutionFeed? feed) : IStopLossMethodResolutionSource
    {
        public List<(DateOnly From, DateOnly To)> Requested { get; } = [];

        public Task<StopLossMethodResolutionFeed?> GetResolutionsAsync(
            DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
        {
            Requested.Add((from, to));
            return Task.FromResult(feed);
        }
    }

    private sealed class ThrowingResolutionSource : IStopLossMethodResolutionSource
    {
        public Task<StopLossMethodResolutionFeed?> GetResolutionsAsync(
            DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("監査台帳へ到達できません");
    }

    private static ReportAutoGenerator NewGenerator(
        IReportStore store, IStopLossMethodUsageSource? usage, IStopLossMethodResolutionSource? resolutions, DateTimeOffset now) =>
        new(store,
            new ReportDraftService(new StubDrafter()),
            new NoOpPeriodFillSource(),
            new FixedClock(now),
            new ReportAutoGenerationSettings(),
            stopLossMethodUsageSource: usage,
            stopLossMethodResolutionSource: resolutions);

    private static StopLossMethodResolved ResolvedAsSelected(OrderApproved a) => new(
        a.DecisionId, a.Intent.Symbol, a.Intent.Market, a.Intent.ProductType, a.StopLossMethod, a.StopLossMethod,
        StopLossMethodResolutionReason.AsSelected, BrokerProvider.MoomooSimulate, a.ApprovedAt.AddSeconds(1));

    // 🔴 否定形: 未注入・照会の失敗はいずれも「照会できませんでした」と書き、未供給の入力として記録する。
    [Fact]
    public async Task T_10_1091_解決結果の供給が無い_失敗なら未供給として描き記録する()
    {
        var usage = new StubUsageSource(StopLossMethodUsage.From([Approved(StopLossExecutionMethod.NoProtectiveStop)]));
        foreach (var source in new IStopLossMethodResolutionSource?[] { null, new ThrowingResolutionSource() })
        {
            var store = new InMemoryReportStore();

            await NewGenerator(store, usage, source, WedAfterClose).RunOnceAsync();

            var daily = DailyOf(store);
            daily.Body.Should().Contain("- **発注執行の解決結果を照会できませんでした（要確認）**");
            daily.UnsuppliedInputs.Should().Contain(ReportInput.StopLossMethodResolutions);
            daily.UnsuppliedInputs.Should().NotContain(ReportInput.StopLossMethods);
        }
    }

    // 対の肯定形: 供給されたら日報の 2 行目に載り、未供給に数えない。照会は当該日報の期間で行う。
    [Fact]
    public async Task T_10_1091_供給された解決結果を日報の2行目へ載せ_当日の期間で照会する()
    {
        var store = new InMemoryReportStore();
        var approved = Approved(StopLossExecutionMethod.NoProtectiveStop);
        var resolutions = new StubResolutionSource(new StopLossMethodResolutionFeed([ResolvedAsSelected(approved)]));

        await NewGenerator(store, new StubUsageSource(StopLossMethodUsage.From([approved])), resolutions, WedAfterClose).RunOnceAsync();

        var daily = DailyOf(store);
        daily.Body.Should().Contain("- **実際に適用された手法（発注執行の解決結果）**: 計 1 件 — S2 逆指値なしの建玉を許容 1 件");
        daily.UnsuppliedInputs.Should().NotContain(ReportInput.StopLossMethodResolutions);
        resolutions.Requested.Should().Contain((new DateOnly(2026, 7, 8), new DateOnly(2026, 7, 8)));
    }

    // 月報でも両入力を当月の期間で引き、§6 に日数を書く。週報は使わない（未供給にも数えない）。
    [Fact]
    public async Task T_10_1091_月報は当月の期間で両入力を引き_週報は未供給に数えない()
    {
        var store = new InMemoryReportStore();
        var approved = Approved(StopLossExecutionMethod.NoProtectiveStop);
        var usage = new StubUsageSource(StopLossMethodUsage.From([approved]));
        var resolutions = new StubResolutionSource(new StopLossMethodResolutionFeed([ResolvedAsSelected(approved)]));

        await NewGenerator(store, usage, resolutions, MonthEndAfterClose).RunOnceAsync();

        var monthly = store.List().Single(r => r.Kind == ReportKind.Monthly);
        monthly.Body.Should().Contain("### 損切りの実行機構（当月）");
        monthly.Body.Should().Contain("- **選択と実際が食い違った日数: 0 日**");
        usage.Requested.Should().Contain((new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31)));
        resolutions.Requested.Should().Contain((new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31)));

        var storeWithout = new InMemoryReportStore();
        await NewGenerator(storeWithout, null, null, MonthEndAfterClose).RunOnceAsync();
        storeWithout.List().Single(r => r.Kind == ReportKind.Monthly).UnsuppliedInputs
            .Should().Contain([ReportInput.StopLossMethods, ReportInput.StopLossMethodResolutions]);
        storeWithout.List().Where(r => r.Kind == ReportKind.Weekly).Should().OnlyContain(r =>
            !r.UnsuppliedInputs.Contains(ReportInput.StopLossMethods)
            && !r.UnsuppliedInputs.Contains(ReportInput.StopLossMethodResolutions));
    }
}
