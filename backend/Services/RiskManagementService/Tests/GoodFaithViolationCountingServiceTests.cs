using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-19, FR-10, FR-11, UC-06, #425, ADR-0025 決定2, IADR-0165:
// **GFV 発生回数の自前計数**の供給経路（約定の事後検出 → 追記台帳 → 判定コアへの供給）。
//
// ★ 数えているのは「**自らのガードをすり抜けた買付**」であり、ブローカーが GFV と判定した件数ではない。
//   **ガードが正しく働けば 1 件も記録されない**（ADR-0025 §理由）。
public class GoodFaithViolationCountingServiceTests
{
    private static readonly DateTimeOffset ExecutedAt = new(2026, 8, 7, 14, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly OccurredOn = new(2026, 8, 7);
    private static readonly Guid DecisionId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static OrderIntent BuyEntry(decimal price = 1_000m, int quantity = 1) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
            quantity, price, PositionEffect.Open);

    private static OrderExecuted Fill(int filledQuantity = 1, decimal averagePrice = 1_000m, string orderId = "ORD-1") =>
        new(DecisionId, orderId, OrderStatus.Filled, filledQuantity, averagePrice, ExecutedAt,
            BrokerProvider.MoomooSimulate);

    private sealed record Fixture(
        GoodFaithViolationCountingService Service,
        InMemoryGoodFaithViolationStore Store,
        InMemoryPortfolioLedgerStore Ledger);

    private static Fixture Build(
        FakeBrokerAccountObservations observations,
        OrderIntent? approvedIntent = null)
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        if (approvedIntent is not null)
        {
            ledger.AppendApproval(DecisionId, approvedIntent, ExecutedAt.AddMinutes(-1));
        }

        var store = new InMemoryGoodFaithViolationStore();
        var clock = new FakeClock(ExecutedAt.AddSeconds(1), OccurredOn);
        return new Fixture(
            new GoodFaithViolationCountingService(ledger, observations, store, clock), store, ledger);
    }

    // =====================================================================================
    // 1. 記録される場合（ガードをすり抜けた買付）
    // =====================================================================================

    // FR-19, FR-11, ADR-0025 決定2: 現金口座で決済済み資金が未供給のまま買付が約定した＝ガードの失敗。
    // 台帳へ 1 件追記し、監査（FR-11）へ運ぶイベントを返す。
    [Fact]
    public void 未決済資金による買付の約定をGFV発生として記録する()
    {
        var fx = Build(FakeBrokerAccountObservations.Cash(), BuyEntry());

        var recorded = fx.Service.Observe(Fill(), OccurredOn);

        recorded.Should().NotBeNull();
        recorded!.OrderId.Should().Be("ORD-1");
        recorded.DecisionId.Should().Be(DecisionId);
        recorded.Symbol.Should().Be("AAPL");
        recorded.PurchaseAmountInBase.Should().Be(1_000m);
        // **決済済み資金は null（未供給）のまま記録する。** 0 と書くと「残高が 0 だった」と読まれる
        // （#424 の表示規約と同じ区別）。
        recorded.SettledCashInBase.Should().BeNull();
        recorded.OccurredOn.Should().Be(OccurredOn);
        fx.Store.GetTally().Count.Should().Be(1);
    }

    // 決済済み資金が供給されていても、それを超える買付が約定すれば 1 件である
    // （**発注前のガードが拒否しようとする事象と同じ**）。
    [Fact]
    public void 決済済み資金を超える買付の約定をGFV発生として記録する()
    {
        var fx = Build(FakeBrokerAccountObservations.Cash(settledCashInBase: 999m), BuyEntry());

        var recorded = fx.Service.Observe(Fill(), OccurredOn);

        recorded.Should().NotBeNull();
        recorded!.SettledCashInBase.Should().Be(999m);
        fx.Store.GetTally().Count.Should().Be(1);
    }

    // 金額は基準通貨（USD）建てで積む。承認 Intent の換算レートを引き継ぐ（IADR-0107 / IADR-0152 と同じ規律）。
    [Fact]
    public void 買付金額は承認Intentの換算レートで基準通貨へ揃える()
    {
        var jpyIntent = new OrderIntent(
            "7203", Market.Japan, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
            10, 3_000m, PositionEffect.Open, StopLossPrice: null, FxRateToBase: 0.0064m);
        var fx = Build(FakeBrokerAccountObservations.Cash(), jpyIntent);

        var recorded = fx.Service.Observe(Fill(filledQuantity: 10, averagePrice: 3_000m), OccurredOn);

        recorded!.PurchaseAmountInBase.Should().Be(10 * 3_000m * 0.0064m);
    }

    // =====================================================================================
    // 2. 記録されない場合（否定形）— **ここが緩むと信用口座の通常の売買が違反として積み上がる**
    // =====================================================================================

    [Fact]
    public void 信用口座の買付は記録しない()
    {
        var fx = Build(FakeBrokerAccountObservations.Margin(), BuyEntry());

        fx.Service.Observe(Fill(), OccurredOn).Should().BeNull();
        fx.Store.GetTally().Count.Should().Be(0);
    }

    // **否定形**: 口座種別を確認できていない状態では記録しない。不明を現金口座とみなすと、
    // 信用口座の通常の回転売買が違反として積み上がり、2 件で恒久的に新規建てが止まる（統制ではなく事故）。
    [Fact]
    public void 口座種別を確認できていなければ記録しない()
    {
        var fx = Build(FakeBrokerAccountObservations.NotObserved(), BuyEntry());

        fx.Service.Observe(Fill(), OccurredOn).Should().BeNull();
        fx.Store.GetTally().Count.Should().Be(0);
    }

    [Fact]
    public void 決済済み資金の範囲内の買付は記録しない()
    {
        var fx = Build(FakeBrokerAccountObservations.Cash(settledCashInBase: 1_000m), BuyEntry());

        fx.Service.Observe(Fill(), OccurredOn).Should().BeNull();
        fx.Store.GetTally().Count.Should().Be(0);
    }

    // 売却は GFV を起こさない（GFV は「未決済の売却代金で買った」ことで起きる）。
    [Fact]
    public void 売却の約定は記録しない()
    {
        var sell = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
            1, 1_000m, PositionEffect.Open);
        var fx = Build(FakeBrokerAccountObservations.Cash(), sell);

        fx.Service.Observe(Fill(), OccurredOn).Should().BeNull();
    }

    // 手仕舞いは記録しない（ADR-0009 の不変条件。止めないものを数えない）。
    [Fact]
    public void 手仕舞いの約定は記録しない()
    {
        var close = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
            1, 1_000m, PositionEffect.Close);
        var fx = Build(FakeBrokerAccountObservations.Cash(), close);

        fx.Service.Observe(Fill(), OccurredOn).Should().BeNull();
    }

    // 約定していない結果（受付・失注・約定 0 の取消）は「買付」ではない。
    [Fact]
    public void 約定していない結果は記録しない()
    {
        var fx = Build(FakeBrokerAccountObservations.Cash(), BuyEntry());

        fx.Service.Observe(Fill(filledQuantity: 0), OccurredOn).Should().BeNull();
    }

    // **否定形**: 承認台帳に相関が無ければ記録しない。金額も方向も分からない約定を推測で違反として記録しない。
    [Fact]
    public void 承認台帳に相関が無ければ記録しない()
    {
        var fx = Build(FakeBrokerAccountObservations.Cash(), approvedIntent: null);

        fx.Service.Observe(Fill(), OccurredOn).Should().BeNull();
        fx.Store.GetTally().Count.Should().Be(0);
    }

    // =====================================================================================
    // 3. 計上単位（1 注文 1 件）と冪等
    // =====================================================================================

    // ADR-0013, IADR-0129 決定10: 同一メッセージの再配送で二重計上しない（主キーが OrderId）。
    [Fact]
    public void 同一注文の再配送は二重計上しない()
    {
        var fx = Build(FakeBrokerAccountObservations.Cash(), BuyEntry());

        fx.Service.Observe(Fill(), OccurredOn);
        fx.Service.Observe(Fill(), OccurredOn);

        fx.Store.GetTally().Count.Should().Be(1);
    }

    // #270, IADR-0113: 部分約定は同一注文の**進行**であり、1 件である
    // （累積数量が増えるたびに数えると、1 回の買付が何件にも化ける）。
    [Fact]
    public void 部分約定の進行は1件として数える()
    {
        var fx = Build(FakeBrokerAccountObservations.Cash(), BuyEntry(quantity: 10));

        fx.Service.Observe(Fill(filledQuantity: 4), OccurredOn);
        fx.Service.Observe(Fill(filledQuantity: 10), OccurredOn);

        fx.Store.GetTally().Count.Should().Be(1);
    }

    // 別の注文は別の件として数える（計上単位が「1 注文」であることの正の対照）。
    [Fact]
    public void 別注文は別件として数える()
    {
        var fx = Build(FakeBrokerAccountObservations.Cash(), BuyEntry());

        fx.Service.Observe(Fill(orderId: "ORD-1"), OccurredOn);
        fx.Service.Observe(Fill(orderId: "ORD-2"), OccurredOn);

        fx.Store.GetTally().Count.Should().Be(2);
    }

    // 2 件に達した時点で新規建てが止まる（ADR-0021 決定4-3・3 件目の手前）。
    [Fact]
    public void 記録が2件に達したら新規建ての停止基準に達する()
    {
        var fx = Build(FakeBrokerAccountObservations.Cash(), BuyEntry());

        fx.Store.GetTally().BlocksNewEntry.Should().BeFalse();
        fx.Service.Observe(Fill(orderId: "ORD-1"), OccurredOn);
        fx.Store.GetTally().BlocksNewEntry.Should().BeFalse();
        fx.Service.Observe(Fill(orderId: "ORD-2"), OccurredOn);
        fx.Store.GetTally().BlocksNewEntry.Should().BeTrue();
    }

    // 期間の明細（監査・報告用）。
    [Fact]
    public void 期間で記録を引ける()
    {
        var fx = Build(FakeBrokerAccountObservations.Cash(), BuyEntry());
        fx.Service.Observe(Fill(), OccurredOn);

        fx.Store.GetRecordedBetween(OccurredOn, OccurredOn).Should().ContainSingle();
        fx.Store.GetRecordedBetween(OccurredOn.AddDays(1), OccurredOn.AddDays(2)).Should().BeEmpty();
    }

    // =====================================================================================
    // 4. 判定コアへの供給（PortfolioSnapshotBuilder）
    // =====================================================================================

    // 台帳が結線されていれば、**0 行でも「0 件を数えた」として供給する**（台帳が権威である）。
    [Fact]
    public void 台帳が結線されていれば0件でも供給する()
    {
        var builder = new PortfolioSnapshotBuilder(
            new FakePortfolioStateProvider(new PortfolioState { Capital = 100_000m }),
            new InMemoryKillSwitchStore(),
            new InMemoryPauseStore(),
            FakeBrokerAccountObservations.Cash(),
            new InMemoryInformationDegradationStore(),
            new InMemoryGoodFaithViolationStore());

        var snapshot = builder.Build();

        snapshot.GoodFaithViolations.Should().Be(GoodFaithViolationTally.Observed(0));
        AccountTypePolicy.BlocksForGoodFaithViolations(snapshot.GoodFaithViolations).Should().BeFalse();
    }

    // **否定形（fail-closed）**: 台帳が結線されていなければ**未供給（null）**であり、
    // 判定コアは現金口座の新規建てを止める。0 件で埋めてはならない。
    [Fact]
    public void 台帳が結線されていなければ未供給のまま渡す()
    {
        var builder = new PortfolioSnapshotBuilder(
            new FakePortfolioStateProvider(new PortfolioState { Capital = 100_000m }),
            new InMemoryKillSwitchStore(),
            new InMemoryPauseStore(),
            FakeBrokerAccountObservations.Cash(),
            new InMemoryInformationDegradationStore());

        var snapshot = builder.Build();

        snapshot.GoodFaithViolations.Should().BeNull();
        AccountTypePolicy.BlocksForGoodFaithViolations(snapshot.GoodFaithViolations).Should().BeTrue();
    }

    // 記録が積まれたらスナップショットにも反映される（供給経路がつながっていることの表明）。
    [Fact]
    public void 記録した件数がスナップショットへ反映される()
    {
        var store = new InMemoryGoodFaithViolationStore();
        var observations = FakeBrokerAccountObservations.Cash();
        var ledger = new InMemoryPortfolioLedgerStore();
        ledger.AppendApproval(DecisionId, BuyEntry(), ExecutedAt.AddMinutes(-1));
        var service = new GoodFaithViolationCountingService(
            ledger, observations, store, new FakeClock(ExecutedAt, OccurredOn));

        service.Observe(Fill(orderId: "ORD-1"), OccurredOn);
        service.Observe(Fill(orderId: "ORD-2"), OccurredOn);

        var snapshot = new PortfolioSnapshotBuilder(
            new FakePortfolioStateProvider(new PortfolioState { Capital = 100_000m }),
            new InMemoryKillSwitchStore(),
            new InMemoryPauseStore(),
            observations,
            new InMemoryInformationDegradationStore(),
            store).Build();

        snapshot.GoodFaithViolations!.Count.Should().Be(2);
        snapshot.GoodFaithViolations.BlocksNewEntry.Should().BeTrue();
    }
}
