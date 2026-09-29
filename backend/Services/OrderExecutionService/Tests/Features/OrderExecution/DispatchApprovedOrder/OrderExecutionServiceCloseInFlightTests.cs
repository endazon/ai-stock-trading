using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.RecordTradeExpenses;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Infrastructure.Steps;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// 🔴 T-10-1752〜T-10-1764, FR-10, FR-05, UC-06, #1105, IADR-0461（IADR-0355 決定2 の拡張）:
// **決済の数量から、同じ建玉を売っている処理中の決済を引く。S0 / S3 の保護レグは引かない。**
//
// 実測（PoC・2026-09-29 UTC）: AAPL 1,428 株を保有（S1 の保護記録 713 株と 715 株）。判断が 1,428 株の売りを決め、
// 直後に S1 が 713 株の成行決済を送った（Accepted）。判断の決済 1,428 株は「Not enough positions」で拒否され、715 株が残った。
// ブローカーの Position.Qty は約定していない売り注文が押さえた株数を引かないため、ゲート（IADR-0355）は 1,428 をそのまま送っていた。
public class OrderExecutionServiceCloseInFlightTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 15, 13, 56, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // 建玉照会と注文照会を持つブローカー（moomoo 相当）。発注は全量約定で返し、送られた注文意図をすべて記録する。
    // 注文照会（GetOrderAsync）は Orders に登録した注文だけを返す（未登録は null＝不明）。
    private sealed class FakeBroker(IReadOnlyList<BrokerPositionSnapshot>? positions)
        : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = positions;

        public List<OrderIntent> Placed { get; } = [];

        public Dictionary<string, BrokerOrder> Orders { get; } = [];

        public HashSet<string> ThrowingOrders { get; } = [];

        public int PositionQueryCount { get; private set; }

        public int OrderQueryCount { get; private set; }

        // 建玉照会の最中に起きること（窓のプローブ）。
        public Action? DuringPositionQuery { get; set; }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default)
        {
            PositionQueryCount++;
            DuringPositionQuery?.Invoke();
            return Task.FromResult(Positions);
        }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            Placed.Add(intent);
            return Task.FromResult(new BrokerOrder(
                "ORD-" + Placed.Count, intent, OrderStatus.Filled, intent.Quantity, intent.Price, Now, Now));
        }

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            throw new InvalidOperationException("本試験では逆指値を送らない");

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default) =>
            PlaceOrderAsync(closeIntent, ct);

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default)
        {
            OrderQueryCount++;
            if (ThrowingOrders.Contains(orderId))
                throw new TimeoutException("注文照会がタイムアウト");
            return Task.FromResult(Orders.TryGetValue(orderId, out var order) ? order : null);
        }

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;

        // ブローカーに生きている（または終端の）注文を登録する。
        public void Live(string orderId, OrderStatus status = OrderStatus.Accepted, int filled = 0) =>
            Orders[orderId] = new BrokerOrder(orderId, Close(0), status, filled, filled > 0 ? 331m : 0m, Now, null);
    }

    private static OrderIntent Close(int qty, TradeSide side = TradeSide.Sell, string symbol = "AAPL",
        Market market = Market.UnitedStates) =>
        new(symbol, market, side, ProductType.Cash, BrokerProvider.MoomooSimulate, qty, 331m, PositionEffect.Close);

    private static OrderApproved Approved(OrderIntent intent) => new(Guid.NewGuid(), intent, intent.Quantity, Now);

    private static BrokerPositionSnapshot Position(int signedQuantity, string symbol = "AAPL") =>
        new(symbol, Market.UnitedStates, signedQuantity, 331m);

    // 発注執行が自分で出した決済の記録（S1 の成行決済など）。
    private static ExecutionRecord CloseRecord(
        string orderId, int qty, OrderStatus status = OrderStatus.Accepted, int filled = 0, Guid? decisionId = null,
        TradeSide side = TradeSide.Sell, string symbol = "AAPL", Market market = Market.UnitedStates) =>
        new(decisionId ?? Guid.NewGuid(), orderId, symbol, market, side, ProductType.Cash, PositionEffect.Close,
            qty, 331m, filled, filled > 0 ? 331m : 0m, status, 0m, Now.AddSeconds(-3));

    // Active な保護記録（S0 / S3＝ブローカー側の逆指値レグ、S1＝ソフトウェア逆指値）。
    private static ProtectiveStopOrder Protection(
        StopLossExecutionMethod mechanism, int qty, string stopOrderId = "", Guid? stopDecisionId = null) =>
        new(Guid.NewGuid(), stopDecisionId ?? Guid.NewGuid(), stopOrderId, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, qty, 300m, 1m, Attempt: 1, ProtectiveStopState.Active,
            Now.AddDays(-1), Now.AddDays(-1), Mechanism: mechanism);

    private sealed record Fixture(
        AppSvc Service, FakeBroker Broker, InMemoryExecutedOrderStore Store,
        InMemoryOrderReservationStore Reservations, InMemoryProtectiveStopOrderStore Stops);

    private static Fixture NewFixture(IReadOnlyList<BrokerPositionSnapshot>? positions, bool withProtectiveStops = true)
    {
        var broker = new FakeBroker(positions);
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        var service = new AppSvc(
            broker, store, reservations, new FakeClock(), withProtectiveStops ? stops : null, null, broker);
        return new Fixture(service, broker, store, reservations, stops);
    }

    // 🔴 T-10-1752（最重要・実測の再現）: 1,428 株保有・S1 の決済 713 株が処理中（Accepted・未約定）・判断が 1,428 株を売る
    // → **715 株を送る**（是正前は 1,428 を送って拒否され、715 株が残った）。乖離イベントは出さない（台帳の乖離ではない）。
    [Fact]
    public async Task 処理中のS1の決済の分を引いて残りだけを送り乖離は出さない()
    {
        var f = NewFixture([Position(1_428)]);
        f.Stops.Save(Protection(StopLossExecutionMethod.SoftwareStop, 713));
        f.Stops.Save(Protection(StopLossExecutionMethod.SoftwareStop, 715));
        var s1Close = CloseRecord("S1-CLOSE", 713);
        f.Store.Save(s1Close);
        f.Broker.Live("S1-CLOSE");
        var approved = Approved(Close(1_428));

        var result = await f.Service.ExecuteAsync(approved);

        f.Broker.Placed.Should().ContainSingle().Which.Quantity.Should().Be(715, "処理中の 713 株はもう売れない");
        result.Executed!.FilledQuantity.Should().Be(715);
        result.Drift.Should().BeNull("台帳の乖離ではない（乖離イベントは使わない）");
        f.Store.FindByDecisionId(approved.DecisionId)!.Quantity.Should().Be(715, "記録も縮めた数量で残す");

        var fact = result.InFlightReduction!;
        fact.DecisionId.Should().Be(approved.DecisionId);
        fact.ApprovedQuantity.Should().Be(1_428);
        fact.BrokerClosableQuantity.Should().Be(1_428);
        fact.InFlightQuantity.Should().Be(713);
        fact.DispatchedQuantity.Should().Be(715);
        fact.InFlightDecisionIds.Should().Equal(s1Close.DecisionId);
        fact.Side.Should().Be(TradeSide.Sell);
    }

    // 🔴 T-10-1753（否定形）: 処理中の決済が決済方向の建玉を**すべて覆う** → 送らない（送れば拒否されるだけ）。
    // 予約の前に見送り、予約表には Forgone を残す（再配送で送らない。IADR-0398）。自動の出し直しはしない（IADR-0211 決定3）。
    [Fact]
    public async Task 処理中の決済が建玉をすべて覆うなら予約の前に見送る()
    {
        var f = NewFixture([Position(713)]);
        f.Store.Save(CloseRecord("S1-CLOSE", 713));
        f.Broker.Live("S1-CLOSE");
        var approved = Approved(Close(713));

        var result = await f.Service.ExecuteAsync(approved);

        f.Broker.Placed.Should().BeEmpty("発注は 1 本も出さない");
        result.Executed.Should().BeNull();
        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.InFlightCloseCoversPosition);
        result.Forgone.Intent.Quantity.Should().Be(713, "見送りは承認が運んだ数量のまま記録する");
        result.Drift.Should().BeNull();
        result.InFlightReduction.Should().BeNull("送っていないので縮めた事実ではない（見送りが事実）");
        f.Reservations.Find(approved.DecisionId)!.State.Should().Be(OrderDispatchState.Forgone);
        f.Store.FindByDecisionId(approved.DecisionId).Should().BeNull();

        // 同じ承認の再配送でも送らない（見送り済み）。
        var again = await f.Service.ExecuteAsync(approved);
        again.ForgoneReplaySuppressed.Should().BeTrue();
        f.Broker.Placed.Should().BeEmpty();
    }

    // 🔴 T-10-1754・T-10-1755（否定形・最重要）: **Active な S0 / S3 の保護レグ（全量・非終端の Close）は引かない。**
    // 引くと S0 の建玉への判断の決済が常に 0 株になり、FR-10「手仕舞いは止めない」に反する。ブローカーが「生きている」と
    // 答える状態でも引かないこと（除外が効いていることを、確かめの照会で消えないように固定する）。
    [Theory]
    [InlineData(StopLossExecutionMethod.BrokerStopOrder)]            // T-10-1754: S0
    [InlineData(StopLossExecutionMethod.AlternativeBrokerOrderType)] // T-10-1755: S3
    public async Task ブローカー側の保護レグは処理中の決済に数えず全量を送る(StopLossExecutionMethod mechanism)
    {
        var f = NewFixture([Position(1_428)]);
        var stopDecisionId = Guid.NewGuid();
        f.Stops.Save(Protection(mechanism, 1_428, stopOrderId: "STOP-1", stopDecisionId: stopDecisionId));
        f.Store.Save(CloseRecord("STOP-1", 1_428, decisionId: stopDecisionId));
        f.Broker.Live("STOP-1");

        var result = await f.Service.ExecuteAsync(Approved(Close(1_428)));

        f.Broker.Placed.Should().ContainSingle().Which.Quantity.Should().Be(1_428, "保護レグは売れる数量を押さえない");
        result.InFlightReduction.Should().BeNull();
        result.Forgone.Should().BeNull();
    }

    // T-10-1754（補足）: 保護レグは注文 ID と DecisionId の**どちらか**で見分ける（張り直しの途中などで片方しか一致しない行）。
    // 保護レグと本物の処理中の決済が並んでいたら、処理中の決済だけを引く。
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task 保護レグは注文IDとDecisionIdのどちらかで見分け処理中の決済だけを引く(bool matchOrderId, bool matchDecisionId)
    {
        var f = NewFixture([Position(1_428)]);
        var legDecisionId = Guid.NewGuid();
        f.Stops.Save(Protection(
            StopLossExecutionMethod.BrokerStopOrder, 1_428,
            stopOrderId: matchOrderId ? "STOP-1" : string.Empty,
            stopDecisionId: matchDecisionId ? legDecisionId : Guid.NewGuid()));
        f.Store.Save(CloseRecord("STOP-1", 1_428, decisionId: legDecisionId));
        f.Broker.Live("STOP-1");
        f.Store.Save(CloseRecord("MKT-CLOSE", 713));
        f.Broker.Live("MKT-CLOSE");

        var result = await f.Service.ExecuteAsync(Approved(Close(1_428)));

        f.Broker.Placed.Should().ContainSingle().Which.Quantity.Should().Be(715);
        result.InFlightReduction!.InFlightQuantity.Should().Be(713);
    }

    // T-10-1756（境界値）: 部分約定した処理中の決済は**未約定の残りだけ**を引く。約定数は記録とブローカーの照会の大きい方を使う
    // （記録は約定追跡が書くまで古い）。713 株のうち 300 株約定 → 413 株を引く。建玉は約定を映して 1,128 株。
    [Theory]
    [InlineData(300, 300)] // 記録もブローカーも 300
    [InlineData(0, 300)]   // 記録はまだ 0（約定追跡の前）・ブローカーは 300
    [InlineData(300, 0)]   // ブローカーの応答が古い（約定数を巻き戻さない）
    public async Task 部分約定した処理中の決済は未約定の残りだけを引く(int recordFilled, int brokerFilled)
    {
        var f = NewFixture([Position(1_128)]);
        f.Store.Save(CloseRecord("S1-CLOSE", 713,
            recordFilled > 0 ? OrderStatus.PartiallyFilled : OrderStatus.Accepted, recordFilled));
        f.Broker.Live("S1-CLOSE", brokerFilled > 0 ? OrderStatus.PartiallyFilled : OrderStatus.Accepted, brokerFilled);

        var result = await f.Service.ExecuteAsync(Approved(Close(1_128)));

        f.Broker.Placed.Should().ContainSingle().Which.Quantity.Should().Be(715);
        result.InFlightReduction!.InFlightQuantity.Should().Be(413);
    }

    // T-10-1757（否定形）: 終端の記録は引かない。ブローカーへの確かめの照会もしない。
    [Theory]
    [InlineData(OrderStatus.Filled)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Rejected)]
    [InlineData(OrderStatus.Expired)]
    public async Task 終端の決済の記録は引かない(OrderStatus terminal)
    {
        var f = NewFixture([Position(1_428)]);
        f.Store.Save(CloseRecord("DONE", 713, terminal, filled: terminal == OrderStatus.Filled ? 713 : 0));
        f.Broker.Live("DONE");

        var result = await f.Service.ExecuteAsync(Approved(Close(1_428)));

        f.Broker.Placed.Should().ContainSingle().Which.Quantity.Should().Be(1_428);
        result.InFlightReduction.Should().BeNull();
        f.Broker.OrderQueryCount.Should().Be(0, "終端の記録は確かめるまでもない");
    }

    // T-10-1758（否定形）: 別の銘柄・別の市場・逆方向（買いの決済）の処理中の決済は引かない。
    [Fact]
    public async Task 別の銘柄と市場と逆方向の処理中の決済は引かない()
    {
        var f = NewFixture([Position(1_428)]);
        f.Store.Save(CloseRecord("MSFT-CLOSE", 713, symbol: "MSFT"));
        f.Store.Save(CloseRecord("JP-CLOSE", 713, market: Market.Japan));
        f.Store.Save(CloseRecord("BUY-CLOSE", 713, side: TradeSide.Buy));
        foreach (var id in new[] { "MSFT-CLOSE", "JP-CLOSE", "BUY-CLOSE" })
            f.Broker.Live(id);

        var result = await f.Service.ExecuteAsync(Approved(Close(1_428)));

        f.Broker.Placed.Should().ContainSingle().Which.Quantity.Should().Be(1_428);
        result.InFlightReduction.Should().BeNull();
    }

    // T-10-1759: 同じ DecisionId の再配送は相 1 で既存結果を再発行する（送らない・建玉も注文も照会し直さない）。
    // 自分の記録を「処理中の決済」として数えることは無い（念のため除外もしている）。
    [Fact]
    public async Task 同じ承認の再配送は既存結果を再発行し送り直さない()
    {
        var f = NewFixture([Position(1_428)]);
        f.Store.Save(CloseRecord("S1-CLOSE", 713));
        f.Broker.Live("S1-CLOSE");
        var approved = Approved(Close(1_428));

        var first = await f.Service.ExecuteAsync(approved);
        var queriesAfterFirst = f.Broker.OrderQueryCount;
        var second = await f.Service.ExecuteAsync(approved);

        f.Broker.Placed.Should().ContainSingle().Which.Quantity.Should().Be(715);
        second.Executed!.OrderId.Should().Be(first.Executed!.OrderId);
        second.InFlightReduction.Should().BeNull("再発行では縮めた事実を出し直さない");
        f.Broker.PositionQueryCount.Should().Be(1);
        f.Broker.OrderQueryCount.Should().Be(queriesAfterFirst);
    }

    // T-10-1760（是正で変えてはいけない側）: 建玉照会の能力が無い発注先（内蔵 paper）では分岐そのものに入らない（IADR-0355 決定4）。
    [Fact]
    public async Task 建玉照会の能力が無い発注先では処理中の決済を引かない()
    {
        var store = new InMemoryExecutedOrderStore();
        store.Save(CloseRecord("S1-CLOSE", 713));
        var service = new AppSvc(
            new PaperBrokerAdapter(), store, new InMemoryOrderReservationStore(), new FakeClock(),
            new InMemoryProtectiveStopOrderStore());

        var result = await service.ExecuteAsync(Approved(Close(1_428)));

        result.Executed!.FilledQuantity.Should().Be(1_428);
        result.InFlightReduction.Should().BeNull();
        result.Forgone.Should().BeNull();
    }

    // 🔴 T-10-1761: 台帳の乖離（Reduce）と処理中の決済が重なる。乖離イベントの数量は**変わらない**（台帳の決済数量とブローカーの
    // ネット）。送るのは両方を引いた数量で、乖離に添える「実際に送った株数」もそれである。
    [Fact]
    public async Task 台帳の乖離と処理中の決済が重なっても乖離の数量は変わらない()
    {
        var f = NewFixture([Position(1_428)]);
        f.Store.Save(CloseRecord("S1-CLOSE", 713));
        f.Broker.Live("S1-CLOSE");

        var result = await f.Service.ExecuteAsync(Approved(Close(3_000)));

        f.Broker.Placed.Should().ContainSingle().Which.Quantity.Should().Be(715);
        var drift = result.Drift!.Drifts.Should().ContainSingle().Subject;
        drift.LedgerQuantity.Should().Be(3_000);
        drift.BrokerQuantity.Should().Be(1_428);
        drift.Kind.Should().Be(PositionDriftKind.QuantityMismatch);
        result.DriftDispatchedQuantity.Should().Be(715);
        result.InFlightReduction!.ApprovedQuantity.Should().Be(3_000);
        result.InFlightReduction.BrokerClosableQuantity.Should().Be(1_428);
        result.InFlightReduction.DispatchedQuantity.Should().Be(715);
    }

    // T-10-1761（補足）: 乖離で縮め、処理中が残りをすべて覆うなら見送る。乖離の記録は失わない。
    [Fact]
    public async Task 台帳の乖離で縮めた後に処理中が覆うなら見送り乖離も残す()
    {
        var f = NewFixture([Position(713)]);
        f.Store.Save(CloseRecord("S1-CLOSE", 713));
        f.Broker.Live("S1-CLOSE");

        var result = await f.Service.ExecuteAsync(Approved(Close(3_000)));

        f.Broker.Placed.Should().BeEmpty();
        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.InFlightCloseCoversPosition);
        result.Drift!.Drifts.Should().ContainSingle().Which.BrokerQuantity.Should().Be(713);
    }

    // 🔴 T-10-1762（窓・規則 11）: 増える側 —— 建玉照会の**最中**に S1 の決済が保存される。処理中は照会の**後**に読むので取りこぼさない。
    [Fact]
    public async Task 窓_建玉照会の最中に保存された処理中の決済も引く()
    {
        var f = NewFixture([Position(1_428)]);
        f.Broker.DuringPositionQuery = () =>
        {
            f.Store.Save(CloseRecord("S1-CLOSE", 713));
            f.Broker.Live("S1-CLOSE");
        };

        var result = await f.Service.ExecuteAsync(Approved(Close(1_428)));

        f.Broker.Placed.Should().ContainSingle().Which.Quantity.Should().Be(715);
    }

    // 🔴 T-10-1762（窓・規則 11）: 減る側 —— 処理中だった S1 の決済が**既に約定し**、建玉照会はそれを映して 715 株。記録は約定追跡が
    // 書く前で Accepted のまま（約定追跡は 30 秒周期）。記録だけを信じると二重に引いて 2 株しか送らない（713 株を取り残す）。
    // ブローカーに確かめ、約定済みなら数えない → 715 株を送る（ゲートの Reduce。是正前と同じ結果）。
    [Theory]
    [InlineData(false)] // 約定は照会より前（記録が古いまま）
    [InlineData(true)]  // 約定は照会の最中（ポーラーが記録を終端にした）
    public async Task 窓_既に約定した処理中の決済は二重に引かない(bool duringQuery)
    {
        var f = NewFixture([Position(715)]);
        f.Store.Save(CloseRecord("S1-CLOSE", 713));
        if (duringQuery)
        {
            f.Broker.Live("S1-CLOSE");
            f.Broker.DuringPositionQuery = () =>
            {
                f.Broker.Live("S1-CLOSE", OrderStatus.Filled, 713);
                f.Store.UpdateOutcome("S1-CLOSE", OrderStatus.Filled, 713, 331m, 0m, Now);
            };
        }
        else
        {
            f.Broker.Live("S1-CLOSE", OrderStatus.Filled, 713);
        }

        var result = await f.Service.ExecuteAsync(Approved(Close(1_428)));

        f.Broker.Placed.Should().ContainSingle().Which.Quantity.Should().Be(715);
        result.InFlightReduction.Should().BeNull("約定済みの決済は処理中ではない");
    }

    // 🔴 T-10-1763（否定形）: ブローカーが「生きている」と確かめられない処理中（照会が null・例外・終端）は引かない
    // ＝是正前と同じ側（拒否され得る）へ倒す。取り残す側（二重に引く）へは倒さない。
    [Theory]
    [InlineData("unknown")]
    [InlineData("throws")]
    [InlineData("cancelled")]
    public async Task 生きていると確かめられない処理中の決済は引かない(string brokerAnswer)
    {
        var f = NewFixture([Position(1_428)]);
        f.Store.Save(CloseRecord("S1-CLOSE", 713));
        switch (brokerAnswer)
        {
            case "throws":
                f.Broker.ThrowingOrders.Add("S1-CLOSE");
                break;
            case "cancelled":
                f.Broker.Live("S1-CLOSE", OrderStatus.Cancelled);
                break;
        }

        var result = await f.Service.ExecuteAsync(Approved(Close(1_428)));

        f.Broker.Placed.Should().ContainSingle().Which.Quantity.Should().Be(1_428);
        result.InFlightReduction.Should().BeNull();
        f.Broker.OrderQueryCount.Should().Be(1);
    }

    // T-10-1764（否定形）: 保護記録ストアが無い構成では保護レグを見分けられないので、何も引かない（是正前と同じ）。
    [Fact]
    public async Task 保護記録ストアが無い構成では処理中の決済を引かない()
    {
        var f = NewFixture([Position(1_428)], withProtectiveStops: false);
        f.Store.Save(CloseRecord("S1-CLOSE", 713));
        f.Broker.Live("S1-CLOSE");

        var result = await f.Service.ExecuteAsync(Approved(Close(1_428)));

        f.Broker.Placed.Should().ContainSingle().Which.Quantity.Should().Be(1_428);
        f.Broker.OrderQueryCount.Should().Be(0);
    }

    // T-10-1752（本番配線）: ハンドラが縮めた事実（CloseReducedForInFlightCloses）を発行し、乖離イベントは発行しない。
    [Fact]
    public async Task 本番配線でも縮めた事実を発行し乖離は発行しない()
    {
        var broker = new FakeBroker([Position(1_428)]);
        var store = new InMemoryExecutedOrderStore();
        store.Save(CloseRecord("S1-CLOSE", 713));
        broker.Live("S1-CLOSE");
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, SystemClock>();
                opts.Services.AddSingleton<IBrokerAdapter>(broker);
                opts.Services.AddSingleton<IBrokerPositionSource>(broker);
                opts.Services.AddSingleton<IExecutedOrderStore>(store);
                opts.Services.AddSingleton<IOrderReservationStore, InMemoryOrderReservationStore>();
                opts.Services.AddSingleton<IProtectiveStopOrderStore, InMemoryProtectiveStopOrderStore>();
                opts.Services.AddSingleton<BusinessMetrics>();
                opts.Services.AddSingleton<AppSvc>();
                opts.Services.AddSingleton<IOrderExpenseSource, UnsuppliedOrderExpenseSource>();
                opts.Services.AddSingleton<TradeExpenseRecordingService>();
                opts.UseAiStockTradingRabbitMq(
                    "ai-stock-trading.order-execution-service", "amqp://guest:guest@localhost:5672",
                    typeof(OrderApprovedHandler).Assembly);
                opts.StubAllExternalTransports();
            })
            .StartAsync();
        var approved = new OrderApproved(Guid.NewGuid(), Close(1_428), 1_428, DateTimeOffset.UtcNow);

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(approved);

        broker.Placed.Should().ContainSingle().Which.Quantity.Should().Be(715);
        session.Sent.MessagesOf<CloseReducedForInFlightCloses>().Should().ContainSingle()
            .Which.DispatchedQuantity.Should().Be(715);
        session.Sent.MessagesOf<PositionReconciliationDrift>().Should().BeEmpty();
        session.Sent.MessagesOf<OrderExecuted>().Should().ContainSingle();

        await host.StopAsync();
    }
}
