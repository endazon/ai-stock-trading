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

// 🔴 T-10-870〜T-10-873, FR-10, UC-01, ADR-0009, #832, IADR-0407: 承認済みの新規建ての判断（TradeDecisionMade）が
// 再配送されても、再審査して拒否へ反転させない。
//
// 事象（#832 項目 2）: #829 / IADR-0346 以降、承認済みで未終端の新規建ては保有建玉数・日次枠・段階資金へ算入される。
// 自分の OrderApproved が台帳と注文アクティビティへ射影された後に同じ判断が再配送されると、**射影済みの自分自身が
// 枠に数えられ**、承認済みの判断が拒否される（同じ DecisionId の OrderRejected が監査・通知へ流れる）。
//
// 本テストは実ハンドラ（台帳・注文アクティビティの射影）＋ 実台帳 ＋ 実射影 ＋ 実スクリーニングの通しで固定する
// （MoomooFillControlRegressionTests と同じ組み立て）。保有建玉数の上限を 1 にし、1 件の未終端の新規建てで
// 次の新規建てが拒否される状態を作る（増える側のプローブ）。
public class ApprovedDecisionReplayRegressionTests
{
    private static readonly DateOnly TradingDay = new(2026, 7, 29);
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 6, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;

        public DateOnly Today => TradingDay;
    }

    // 米国株・内蔵 paper（口座種別を要求しない）。10 株 × $10 ＝ $100（他の金額上限に掛からない）。
    private static OrderIntent Entry(string symbol = "AAPL") =>
        new(symbol, Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 10m);

    private static OrderIntent Close(string symbol = "AAPL") =>
        new(symbol, Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.InternalPaper, 10, 10m,
            PositionEffect.Close);

    private sealed record Stores(InMemoryPortfolioLedgerStore Ledger, InMemoryOrderActivityStore Activity);

    private static Stores NewStores() => new(new InMemoryPortfolioLedgerStore(), new InMemoryOrderActivityStore());

    // 保有建玉数の上限を 1 に絞る（未終端の新規建て 1 件で次の新規建てが MaxPositionsExceeded になる）。
    private static RiskManagementSettings OnePosition()
    {
        var defaults = TradingDefaults.CreateSettings();
        return defaults with { Limits = defaults.Limits with { MaxOpenPositions = 1 } };
    }

    // 自分の OrderApproved を射影する本番のハンドラ（台帳・注文アクティビティ）。
    private static Task<IHost> BuildProjectionHostAsync(Stores stores) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IPortfolioLedgerStore>(stores.Ledger);
                opts.Services.AddSingleton<IOrderActivityStore>(stores.Activity);
                opts.Services.AddSingleton<IRecognitionFxRateResolver>(new StubRecognitionFxRateResolver());
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<OrderApprovedLedgerHandler>()
                    .IncludeType<OrderApprovedActivityHandler>();
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static OrderScreeningService BuildScreening(Stores stores, ILockoutStore lockout)
    {
        var clock = new FixedClock();
        var provider = new LedgerPortfolioStateProvider(
            stores.Ledger, new InMemoryWorkingEntryOrderSource(stores.Ledger, stores.Activity), clock);
        var snapshotBuilder = new PortfolioSnapshotBuilder(
            provider, new InMemoryKillSwitchStore(), new InMemoryPauseStore(),
            new InMemoryBrokerAccountObservationStore(TimeProvider.System),
            FakeInformationDegradation.Affirmed(), capitalBaseline: FakeCapitalBaseline.Of(TradingDefaults.InitialCapital));
        return new OrderScreeningService(
            new InMemoryRiskSettingsStore(OnePosition()), snapshotBuilder, lockout, clock,
            new WeekendBusinessCalendar(), new InMemoryBuyInInferenceStore(), stores.Ledger);
    }

    // 審査 → 承認の発行 → 自分の射影（本番の順序）。承認された判断を返す。
    private static async Task<TradeDecisionMade> ApproveAndProjectAsync(
        IHost host, OrderScreeningService screening, OrderIntent intent)
    {
        var decision = new TradeDecisionMade(Guid.NewGuid(), intent, "最初の配送", Now);
        var first = screening.Screen(decision);
        first.IsApproved.Should().BeTrue("前提: 最初の審査は承認される");
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(first.Approved!);
        return decision;
    }

    // 🔴 T-10-870（否定形・最重要）: 射影済みの承認済み新規建ての再配送は拒否へ反転しない。
    // 是正前はここで MaxPositionsExceeded の拒否が返った（射影済みの自分自身が保有建玉数に数えられる）。
    [Fact]
    public async Task 承認済みの新規建ての判断が再配送されても拒否へ反転しない()
    {
        var stores = NewStores();
        using var host = await BuildProjectionHostAsync(stores);
        var screening = BuildScreening(stores, new InMemoryLockoutStore());
        var decision = await ApproveAndProjectAsync(host, screening, Entry());

        var replay = screening.Screen(decision);

        replay.Rejected?.Reasons.Should().BeEmpty("承認済みの判断を拒否へ反転させない（是正前は MaxPositionsExceeded）");
        replay.Rejected.Should().BeNull("承認済みの判断を拒否へ反転させない");
        replay.IsApprovedReplay.Should().BeTrue("承認済みの判断は再審査しない");
        replay.Approved.Should().BeNull("承認を発行し直さない（承認時点の設定を再構成できない）");
        replay.IsApproved.Should().BeFalse("第 3 の形は承認の形ではない（呼び出し側は先に IsApprovedReplay を見る）");
        replay.Observation.DecisionId.Should().Be(decision.DecisionId);
        replay.Observation.RejectionReasons.Should().BeEmpty();

        await host.StopAsync();
    }

    // 🔴 T-10-871（否定形・減る側 a）: 抑止は同じ DecisionId に限る。別の判断は同じ状態で従来どおり拒否される。
    [Fact]
    public async Task 別の判断の新規建ては同じ状態で従来どおり拒否される()
    {
        var stores = NewStores();
        using var host = await BuildProjectionHostAsync(stores);
        var screening = BuildScreening(stores, new InMemoryLockoutStore());
        await ApproveAndProjectAsync(host, screening, Entry());

        var other = screening.Screen(new TradeDecisionMade(Guid.NewGuid(), Entry("MSFT"), "別の判断", Now));

        other.IsApprovedReplay.Should().BeFalse();
        other.IsApproved.Should().BeFalse("未終端の新規建て 1 件で保有建玉数の上限 1 に達している");
        other.Rejected!.Reasons.Should().Contain(RejectionReason.MaxPositionsExceeded);

        await host.StopAsync();
    }

    // T-10-872（減る側 b・ADR-0009）: 承認済みの手仕舞い（Close）の再配送は抑止せず、従来どおり再審査して承認する
    // （承認を発行し直しても発注執行が DecisionId で止める。抑止すると届かなかった手仕舞いが出ない側へ倒れる）。
    [Fact]
    public async Task 承認済みの手仕舞いの再配送は抑止せず再審査して承認する()
    {
        var stores = NewStores();
        using var host = await BuildProjectionHostAsync(stores);
        var screening = BuildScreening(stores, new InMemoryLockoutStore());
        var decision = await ApproveAndProjectAsync(host, screening, Close());
        stores.Ledger.FindApprovedPositionEffect(decision.DecisionId).Should().Be(PositionEffect.Close, "前提: 射影済み");

        var replay = screening.Screen(decision);

        replay.IsApprovedReplay.Should().BeFalse("手仕舞いは抑止の対象外");
        replay.IsApproved.Should().BeTrue();
        replay.Approved!.DecisionId.Should().Be(decision.DecisionId);

        await host.StopAsync();
    }

    // T-10-873（減る側 c・窓の手前）: 射影の前（台帳に承認行が無い）に届いた再配送は通常の審査を受ける。
    // 自分はまだ枠に数えられていないので、最初と同じく承認される（発注執行の予約が DecisionId で 2 本目を止める）。
    [Fact]
    public void 射影の前に届いた再配送は通常の審査を受ける()
    {
        var stores = NewStores();
        var screening = BuildScreening(stores, new InMemoryLockoutStore());
        var decision = new TradeDecisionMade(Guid.NewGuid(), Entry(), "最初の配送", Now);
        screening.Screen(decision).IsApproved.Should().BeTrue();

        var replay = screening.Screen(decision);

        replay.IsApprovedReplay.Should().BeFalse("承認行が無ければ承認済みとは言えない（推測で抑止しない）");
        replay.IsApproved.Should().BeTrue();
        replay.Approved!.DecisionId.Should().Be(decision.DecisionId);
    }
}
