using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Infrastructure.ExternalServices;
using RiskManagementService.Common.Abstractions;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, #935, IADR-0394: **2026-09-23 の再現**を、実ハンドラ（台帳・注文アクティビティ）＋実台帳＋実射影＋
// 実スクリーニングの通しで固定する（合成の要所を実物のまま。MoomooFillControlRegressionTests と同じ作法）。
//
// 実測: 13:43:33Z に S1 が AAPL 707 株を損切り → 13:46:45Z に判断エンジンが同じ AAPL を 715 株新規買い → 承認・約定。
// 是正前はこの買いを止めるものが何も無かった（既存の差金決済防止は信用口座の米国株に掛からない）。
// 数量は統制上限（equity 100,000 の 25%）の内側に縮めてある——本回帰が見るのは時刻と由来であり金額ではない。
public class StopOutReentryRegressionTests
{
    private static readonly DateTimeOffset EntryFilledAt = new(2026, 9, 23, 13, 30, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset StopOutAt = new(2026, 9, 23, 13, 43, 33, TimeSpan.Zero);
    private static readonly DateTimeOffset BuyAttemptAt = new(2026, 9, 23, 13, 46, 45, TimeSpan.Zero);

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;

        public DateOnly Today => TradingDay.Of(UtcNow);
    }

    private sealed record Stores(InMemoryPortfolioLedgerStore Ledger, InMemoryOrderActivityStore Activity);

    private static OrderIntent Buy(int quantity = 7) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
            quantity, 337.63m, PositionEffect.Open, StopLossPrice: 330m);

    private static OrderIntent SellClose(int quantity = 7, bool marketOrder = false) =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.InternalPaper,
            quantity, 337.455m, PositionEffect.Close, MarketOrder: marketOrder);

    private static Task<IHost> BuildHostAsync(Stores stores) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IPortfolioLedgerStore>(stores.Ledger);
                opts.Services.AddSingleton<IOrderActivityStore>(stores.Activity);
                opts.Services.AddSingleton<IRecognitionFxRateResolver>(new StubRecognitionFxRateResolver());
                // 台帳へ承認行を書く 4 経路すべて（由来はそれぞれのハンドラが決める）＋約定。
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<OrderApprovedLedgerHandler>()
                    .IncludeType<OrderExecutedLedgerHandler>()
                    .IncludeType<SoftwareStopExecutedLedgerHandler>()
                    .IncludeType<ProtectiveStopPlacedLedgerHandler>()
                    .IncludeType<ProtectiveStopCoverageLostLedgerHandler>()
                    .IncludeType<OrderApprovedActivityHandler>()
                    .IncludeType<OrderExecutedActivityHandler>();
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    // 台帳 → 射影 → スナップショット → スクリーニング（本番と同じ組み立て。台帳は損切りの入力としても渡す）。
    private static OrderScreeningService BuildScreening(Stores stores, IClock clock)
    {
        var provider = new LedgerPortfolioStateProvider(
            stores.Ledger, new InMemoryWorkingEntryOrderSource(stores.Ledger, stores.Activity), clock);
        var snapshotBuilder = new PortfolioSnapshotBuilder(
            provider, new InMemoryKillSwitchStore(), new InMemoryPauseStore(),
            new InMemoryBrokerAccountObservationStore(TimeProvider.System),
            FakeInformationDegradation.Affirmed(), capitalBaseline: FakeCapitalBaseline.Of(100_000m));
        return new OrderScreeningService(
            new InMemoryRiskSettingsStore(), snapshotBuilder, new InMemoryLockoutStore(), clock,
            new WeekendBusinessCalendar(), new InMemoryBuyInInferenceStore(), stores.Ledger);
    }

    private static async Task InvokeAsync<T>(IHost host, T message)
        where T : notnull =>
        (await host.TrackActivityForTest().InvokeMessageAndWaitAsync(message))
            .Executed.MessagesOf<T>().Should().NotBeEmpty();

    // 建玉を作る（判断由来の新規建て → 約定）。
    private static async Task HoldAsync(IHost host, int quantity = 7)
    {
        var entryId = Guid.NewGuid();
        await InvokeAsync(host, new OrderApproved(entryId, Buy(quantity), quantity, EntryFilledAt.AddSeconds(-5)));
        await InvokeAsync(host, new OrderExecuted(
            entryId, "ENTRY-" + entryId, OrderStatus.Filled, quantity, 340m, EntryFilledAt, BrokerProvider.MoomooSimulate));
    }

    private static TradeDecisionMade Decision(OrderIntent intent, DateTimeOffset at) =>
        new(Guid.NewGuid(), intent, "判断", at);

    // T-10-775: 9/23 の時系列。S1 の発動の 3 分後の買いは止まり、翌 ET 日には通る。
    [Fact]
    public async Task 九月二十三日のS1損切りの三分後の買い直しは拒否され翌取引日には通る()
    {
        var stores = new Stores(new InMemoryPortfolioLedgerStore(), new InMemoryOrderActivityStore());
        using var host = await BuildHostAsync(stores);
        await HoldAsync(host);

        // S1 の発動（成行決済をブローカーが受理）→ 数秒後に約定。
        var closeId = Guid.NewGuid();
        await InvokeAsync(host, new SoftwareStopExecuted(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, SoftwareStopOutcome.ClosePlaced, 7, 337.50m, 337.455m, 1,
            closeId, "S1-CLOSE", SellClose(marketOrder: true), StopOutAt));
        await InvokeAsync(host, new OrderExecuted(
            closeId, "S1-CLOSE", OrderStatus.Filled, 7, 337.455m, StopOutAt.AddSeconds(4), BrokerProvider.MoomooSimulate));

        var clock = new MutableClock(BuyAttemptAt);
        var screening = BuildScreening(stores, clock);

        var rejected = screening.Screen(Decision(Buy(), BuyAttemptAt));
        rejected.IsApproved.Should().BeFalse();
        rejected.Rejected!.Reasons.Should().ContainSingle().Which.Should().Be(RejectionReason.StoppedOutSameDay);

        // 5 分後の 2 本目も止まる。JST の日付が変わった後（ET はまだ 9/23）も止まる。
        clock.UtcNow = BuyAttemptAt.AddMinutes(5);
        screening.Screen(Decision(Buy(), clock.UtcNow)).IsApproved.Should().BeFalse();
        clock.UtcNow = new DateTimeOffset(2026, 9, 23, 15, 30, 0, TimeSpan.Zero);
        screening.Screen(Decision(Buy(), clock.UtcNow)).IsApproved.Should().BeFalse();

        // 翌 ET 日（9/24 00:00 EDT 以降）は通る。
        clock.UtcNow = new DateTimeOffset(2026, 9, 24, 13, 43, 33, TimeSpan.Zero);
        screening.Screen(Decision(Buy(), clock.UtcNow)).IsApproved.Should().BeTrue();

        await host.StopAsync();
    }

    // T-10-775 / T-10-771: 約定を待たない。S1 の発動（承認）だけで、約定が台帳へ届く前から止める。
    [Fact]
    public async Task S1の決済の約定が届く前から同方向の新規建ては止まる()
    {
        var stores = new Stores(new InMemoryPortfolioLedgerStore(), new InMemoryOrderActivityStore());
        using var host = await BuildHostAsync(stores);
        await HoldAsync(host);

        await InvokeAsync(host, new SoftwareStopExecuted(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, SoftwareStopOutcome.ClosePlaced, 7, 337.50m, 337.455m, 1,
            Guid.NewGuid(), "S1-CLOSE", SellClose(marketOrder: true), StopOutAt));

        var screening = BuildScreening(stores, new MutableClock(BuyAttemptAt));

        screening.Screen(Decision(Buy(), BuyAttemptAt)).Rejected!.Reasons
            .Should().Contain(RejectionReason.StoppedOutSameDay);

        await host.StopAsync();
    }

    // T-10-774: 損切りの当日でも手仕舞い（Close）は止めない（残りの建玉を判断由来で閉じる）。
    [Fact]
    public async Task 損切りの当日でも手仕舞いは通る()
    {
        var stores = new Stores(new InMemoryPortfolioLedgerStore(), new InMemoryOrderActivityStore());
        using var host = await BuildHostAsync(stores);
        await HoldAsync(host, quantity: 14);

        // 14 株のうち 7 株だけ S1 が決済した（残り 7 株を保有）。
        var closeId = Guid.NewGuid();
        await InvokeAsync(host, new SoftwareStopExecuted(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, SoftwareStopOutcome.ClosePlaced, 7, 337.50m, 337.455m, 1,
            closeId, "S1-CLOSE", SellClose(marketOrder: true), StopOutAt));
        await InvokeAsync(host, new OrderExecuted(
            closeId, "S1-CLOSE", OrderStatus.Filled, 7, 337.455m, StopOutAt.AddSeconds(4), BrokerProvider.MoomooSimulate));

        var outcome = BuildScreening(stores, new MutableClock(BuyAttemptAt))
            .Screen(Decision(SellClose(), BuyAttemptAt));

        outcome.IsApproved.Should().BeTrue();

        await host.StopAsync();
    }

    // T-10-771: S0（ブローカー側逆指値）は**約定**で数える。武装しただけでは止めず、当日に約定したら止める。
    [Fact]
    public async Task S0は逆指値の約定で損切りと数え武装だけでは止めない()
    {
        var stores = new Stores(new InMemoryPortfolioLedgerStore(), new InMemoryOrderActivityStore());
        using var host = await BuildHostAsync(stores);
        await HoldAsync(host);

        // 数日前に武装した逆指値（エントリーと同時の発注）。
        var stopId = Guid.NewGuid();
        await InvokeAsync(host, new ProtectiveStopPlaced(
            Guid.NewGuid(), stopId, "S0-STOP", SellClose(), 337.50m, 1, EntryFilledAt.AddDays(-3)));

        var clock = new MutableClock(StopOutAt.AddMinutes(-1));
        var screening = BuildScreening(stores, clock);
        screening.Screen(Decision(Buy(), clock.UtcNow)).IsApproved.Should().BeTrue("武装しただけでは損切りは成立していない");

        await InvokeAsync(host, new OrderExecuted(
            stopId, "S0-STOP", OrderStatus.Filled, 7, 337.45m, StopOutAt, BrokerProvider.MoomooSimulate));

        clock.UtcNow = BuyAttemptAt;
        screening.Screen(Decision(Buy(), clock.UtcNow)).Rejected!.Reasons
            .Should().ContainSingle().Which.Should().Be(RejectionReason.StoppedOutSameDay);

        await host.StopAsync();
    }

    // T-10-772: 保護喪失の成行手仕舞い・owner の手仕舞い（OrderApproved 経由）は損切りと数えない。
    [Fact]
    public async Task 保護喪失の手仕舞いとOrderApproved経由の手仕舞いは損切りと数えない()
    {
        var stores = new Stores(new InMemoryPortfolioLedgerStore(), new InMemoryOrderActivityStore());
        using var host = await BuildHostAsync(stores);
        await HoldAsync(host, quantity: 14);

        var lostCloseId = Guid.NewGuid();
        await InvokeAsync(host, new ProtectiveStopCoverageLost(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, ProtectiveStopLossCause.LapsedInFlight,
            ProtectiveStopRemediation.PositionClosed, 7, lostCloseId, SellClose(marketOrder: true), StopOutAt));
        await InvokeAsync(host, new OrderExecuted(
            lostCloseId, "LOST-CLOSE", OrderStatus.Filled, 7, 337.4m, StopOutAt.AddSeconds(3), BrokerProvider.MoomooSimulate));

        var ownerCloseId = Guid.NewGuid();
        await InvokeAsync(host, new OrderApproved(ownerCloseId, SellClose(marketOrder: true), 7, StopOutAt.AddSeconds(10)));
        await InvokeAsync(host, new OrderExecuted(
            ownerCloseId, "OWNER-CLOSE", OrderStatus.Filled, 7, 337.3m, StopOutAt.AddSeconds(12), BrokerProvider.MoomooSimulate));

        BuildScreening(stores, new MutableClock(BuyAttemptAt))
            .Screen(Decision(Buy(), BuyAttemptAt)).IsApproved.Should().BeTrue();

        await host.StopAsync();
    }

    // T-10-773: 由来が記録されていない当日の決済（本変更より前に書かれた承認行の形）は不明として止める。
    [Fact]
    public void 由来の無い当日の決済があれば同方向の新規建ては不明の理由で止まる()
    {
        var stores = new Stores(new InMemoryPortfolioLedgerStore(), new InMemoryOrderActivityStore());
        var closeId = Guid.NewGuid();
        stores.Ledger.AppendApproval(closeId, SellClose(), StopOutAt); // 由来を渡さない＝列追加前の行と同じ
        stores.Ledger.AppendFill(closeId, "LEGACY", 7, 337.455m, StopOutAt.AddSeconds(4));

        BuildScreening(stores, new MutableClock(BuyAttemptAt))
            .Screen(Decision(Buy(), BuyAttemptAt)).Rejected!.Reasons
            .Should().ContainSingle().Which.Should().Be(RejectionReason.StopOutStatusUnknown);
    }
}
