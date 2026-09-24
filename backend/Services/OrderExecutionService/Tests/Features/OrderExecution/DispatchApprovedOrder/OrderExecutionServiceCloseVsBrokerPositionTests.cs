using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;
using AwesomeAssertions;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;
using OrderExecutionService.Infrastructure.Persistence;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// 🔴 T-10-494〜T-10-502・T-10-505・T-10-517・T-10-519, FR-10, FR-05, ADR-0016, UC-06, #864, IADR-0355:
// **決済（Close）をブローカーの実建玉と突き合わせてから送る。**
//
// 是正前の穴: 決済の数量の出所は台帳の射影であってブローカーの事実ではない（IADR-0119 決定1 / IADR-0351 決定6）。
// 台帳が乖離していると（#849。2026-09-18 に台帳 3,381 株 / ブローカー 0 株を実測）、決済注文は**ブローカー上では
// 保有 0 からの売り＝裸の新規ショート**になる。空売りは方針で禁止であり、注文が「決済」として通るため
// ショート建玉の規律も空売り固有の統制も効かない。
public class OrderExecutionServiceCloseVsBrokerPositionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 6, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // 建玉照会の能力を持つブローカー（moomoo 相当）。snapshot が null なら「照会不能（不明）」を返す。
    // 発注は要求どおりの全量約定で返し、**送られた注文意図をすべて記録する**（数量が縮んだことの検証に使う）。
    private sealed class FakePositionAwareBroker(IReadOnlyList<BrokerPositionSnapshot>? snapshot)
        : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public List<OrderIntent> Placed { get; } = [];

        public int PositionQueryCount { get; private set; }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default)
        {
            PositionQueryCount++;
            return Task.FromResult(snapshot);
        }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            Placed.Add(intent);
            return Task.FromResult(new BrokerOrder(
                "ORD-" + Placed.Count, intent, OrderStatus.Filled, intent.Quantity, intent.Price, Now, Now));
        }

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            Task.FromResult(new BrokerOrder(
                "STOP-" + closeIntent.Symbol, closeIntent, OrderStatus.Accepted, 0, 0m, Now, null));

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default) =>
            Task.FromResult(new BrokerOrder(
                "MKT-" + closeIntent.Symbol, closeIntent, OrderStatus.Filled,
                closeIntent.Quantity, closeIntent.Price, Now, Now));

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static OrderIntent CloseIntent(int qty = 3_381, TradeSide side = TradeSide.Sell) =>
        new("AAPL", Market.UnitedStates, side, ProductType.Cash, BrokerProvider.MoomooSimulate,
            qty, 100m, PositionEffect.Close);

    private static OrderIntent OpenIntent(int qty = 10) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
            qty, 100m, PositionEffect.Open, StopLossPrice: 95m);

    private static OrderApproved Approved(OrderIntent intent) => new(Guid.NewGuid(), intent, intent.Quantity, Now);

    private static BrokerPositionSnapshot Position(int signedQuantity) =>
        new("AAPL", Market.UnitedStates, signedQuantity, 100m);

    private static (AppSvc Service, InMemoryExecutedOrderStore Store, InMemoryOrderReservationStore Reservations)
        NewService(FakePositionAwareBroker broker)
    {
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        return (new AppSvc(broker, store, reservations, new FakeClock(), null, null, broker), store, reservations);
    }

    // 🔴 T-10-494（否定形・最重要）: **台帳に建玉があり、ブローカーに無い。** #849 の実測そのもの
    //（台帳 3,381 株 / ブローカー 0 株）。是正前はここで売り注文が飛び、**裸の新規ショート**になっていた。
    [Fact]
    public async Task ブローカーに建玉が無い決済は発注されず見送りと乖離が残る()
    {
        var broker = new FakePositionAwareBroker([]); // 空列＝「ブローカーは持っていない」という観測事実
        var (service, store, reservations) = NewService(broker);
        var approved = Approved(CloseIntent());

        var result = await service.ExecuteAsync(approved);

        broker.Placed.Should().BeEmpty("保有 0 からの売り（裸のショート）を出してはならない");
        result.Executed.Should().BeNull();
        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.BrokerPositionAbsent);
        store.GetAll().Should().BeEmpty();
        reservations.Find(approved.DecisionId)!.State.Should().Be(
            OrderDispatchState.Forgone, "送らないと決めた注文は予約を取らず、見送りの記録だけを残す（#876。再配送で送らない）");

        // 監査・通知は既存の乖離検知と同じイベントで残す（新しい経路を作らない）。
        var drift = result.Drift!.Drifts.Should().ContainSingle().Subject;
        drift.Symbol.Should().Be("AAPL");
        drift.LedgerQuantity.Should().Be(3_381);
        drift.BrokerQuantity.Should().Be(0);
        drift.Kind.Should().Be(PositionDriftKind.LedgerOnly);
    }

    // 🔴 T-10-495（否定形・最重要）: **実建玉に満たない決済は実建玉の範囲へ縮めて送る。**
    // 見送りに倒すと、実在する 100 株の手仕舞いまで塞いでしまう（FR-10 に反する）。
    [Fact]
    public async Task 実建玉に満たない決済は実建玉の範囲へ縮めて発注され乖離が残る()
    {
        var broker = new FakePositionAwareBroker([Position(100)]);
        var (service, store, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(CloseIntent(qty: 300)));

        broker.Placed.Should().ContainSingle().Which.Quantity.Should().Be(100, "実建玉を超える分は送らない");
        result.Executed!.FilledQuantity.Should().Be(100);
        store.GetAll().Should().ContainSingle().Which.Quantity.Should().Be(100, "記録も縮めた数量で残す");

        var drift = result.Drift!.Drifts.Should().ContainSingle().Subject;
        drift.LedgerQuantity.Should().Be(300);
        drift.BrokerQuantity.Should().Be(100);
        drift.Kind.Should().Be(PositionDriftKind.QuantityMismatch);
    }

    // 🔴 T-10-496（否定形・最重要）: **建玉を照会できない（null＝不明）ときは送らない。**
    // 空列（建玉ゼロ）と取り違えず、理由も別に持つ（IADR-0355 決定3）。
    [Fact]
    public async Task 建玉を照会できないときの決済は見送り乖離は発行しない()
    {
        var broker = new FakePositionAwareBroker(null);
        var (service, store, reservations) = NewService(broker);
        var approved = Approved(CloseIntent(qty: 300));

        var result = await service.ExecuteAsync(approved);

        broker.Placed.Should().BeEmpty();
        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.BrokerPositionsIndeterminate);
        result.Forgone.Intent.Quantity.Should().Be(300, "見送りは承認が運んだ数量のまま記録する");
        result.Drift.Should().BeNull("乖離を確認できていない（不明を乖離として報告しない）");
        store.GetAll().Should().BeEmpty();
        reservations.Find(approved.DecisionId)!.State.Should().Be(OrderDispatchState.Forgone); // #876
    }

    // T-10-497（是正で**変えてはいけない**側）: 台帳とブローカーが一致している通常時は挙動が変わらない。
    // ブローカーの方が多い場合も、送るのは承認された数量だけである（勝手に増やさない）。
    [Theory]
    [InlineData(300)] // 一致
    [InlineData(500)] // ブローカーの方が多い（部分決済）
    public async Task 実建玉が足りている決済は従来どおり全量が発注され乖離は出ない(int held)
    {
        var broker = new FakePositionAwareBroker([Position(held)]);
        var (service, store, reservations) = NewService(broker);
        var approved = Approved(CloseIntent(qty: 300));

        var result = await service.ExecuteAsync(approved);

        broker.Placed.Should().ContainSingle().Which.Quantity.Should().Be(300);
        result.Executed!.Status.Should().Be(OrderStatus.Filled);
        result.Drift.Should().BeNull();
        store.GetAll().Should().ContainSingle().Which.PositionEffect.Should().Be(PositionEffect.Close);
        reservations.Find(approved.DecisionId)!.CompletedAt.Should().NotBeNull();
    }

    // T-10-498（是正で**変えてはいけない**側）: **能力の無いブローカー（内蔵 paper）では照合しない。**
    // 建玉照会の実装が無い発注先では依存そのものが DI に現れない（構造的な非干渉）。
    [Fact]
    public async Task 建玉照会の能力が無い発注先では従来どおり照合せずに決済を送る()
    {
        var store = new InMemoryExecutedOrderStore();
        var service = new AppSvc(
            new PaperBrokerAdapter(), store, new InMemoryOrderReservationStore(), new FakeClock());

        var result = await service.ExecuteAsync(Approved(CloseIntent(qty: 300)));

        result.Executed!.Status.Should().Be(OrderStatus.Filled);
        result.Forgone.Should().BeNull();
        result.Drift.Should().BeNull();
        store.GetAll().Should().ContainSingle().Which.Quantity.Should().Be(300);
    }

    // T-10-499（是正で**変えてはいけない**側）: **新規建て（Open）は突き合わせない。**
    // 建玉が無いのは新規建てでは正常であり、ここで止めると 1 本も建てられなくなる。
    [Fact]
    public async Task 新規建ては建玉を照会せずに従来どおり発注される()
    {
        var broker = new FakePositionAwareBroker([]);
        var (service, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(OpenIntent()));

        result.Executed!.Status.Should().Be(OrderStatus.Filled);
        broker.Placed.Should().ContainSingle().Which.PositionEffect.Should().Be(PositionEffect.Open);
        broker.PositionQueryCount.Should().Be(0, "Open では照会そのものを行わない");
        result.Drift.Should().BeNull();
    }

    // 🔴 T-10-500（否定形）: **反対方向の建玉は決済に使えない。** ショートを持っているところへ
    // さらに売れば、決済ではなくショートの積み増しになる。
    [Fact]
    public async Task 反対方向の建玉しか無い決済は発注されない()
    {
        var broker = new FakePositionAwareBroker([Position(-100)]); // ショート 100 株
        var (service, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(CloseIntent(qty: 100))); // 売りの決済

        broker.Placed.Should().BeEmpty();
        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.BrokerPositionAbsent);
        result.Drift!.Drifts.Should().ContainSingle().Which.BrokerQuantity.Should().Be(-100);
    }

    // T-10-501: ショート建玉の決済（買い戻し）にも同じ規律が効く（向きだけが反転する）。
    [Fact]
    public async Task ショート建玉の決済も実建玉の範囲へ縮めて発注される()
    {
        var broker = new FakePositionAwareBroker([Position(-40)]);
        var (service, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(CloseIntent(qty: 100, side: TradeSide.Buy)));

        broker.Placed.Should().ContainSingle().Which.Quantity.Should().Be(40);
        var drift = result.Drift!.Drifts.Should().ContainSingle().Subject;
        drift.LedgerQuantity.Should().Be(-100);
        drift.BrokerQuantity.Should().Be(-40);
    }

    // T-10-502: 同一 DecisionId の再処理（メッセージ再配送）では**照会もしない**。
    // 相 1（完了の権威）で既存結果を返す経路は、ブローカーの建玉が後から変わっていても影響を受けない。
    [Fact]
    public async Task 再処理では建玉を照会せず既存結果を返す()
    {
        var broker = new FakePositionAwareBroker([Position(300)]);
        var (service, _, _) = NewService(broker);
        var approved = Approved(CloseIntent(qty: 300));

        await service.ExecuteAsync(approved);
        var second = await service.ExecuteAsync(approved);

        broker.Placed.Should().ContainSingle("再発注しない");
        broker.PositionQueryCount.Should().Be(1, "再処理では照会し直さない");
        second.Executed!.OrderId.Should().Be("ORD-1");
    }

    // ---- T-10-505: 突き合わせの純関数（境界値。IADR-0355 決定2・決定3）----

    [Theory]
    [InlineData(0, 100, BrokerHeldPositionOutcome.NoPosition, 0)]      // 建玉ゼロ
    [InlineData(99, 100, BrokerHeldPositionOutcome.Reduce, 99)]        // 1 株足りない（境界）
    [InlineData(100, 100, BrokerHeldPositionOutcome.Proceed, 100)]     // ちょうど（境界）
    // 🔴 1 株多い（境界）: **注文数量で切らず、実際の建玉数 101 が入る**（#873 の監査〔3 巡目〕1）。
    [InlineData(101, 100, BrokerHeldPositionOutcome.Proceed, 101)]
    [InlineData(-100, 100, BrokerHeldPositionOutcome.NoPosition, 0)]   // 反対方向
    public void 売りの決済は決済方向の建玉だけを数える(
        int netQuantity, int orderQuantity, BrokerHeldPositionOutcome expected, int closable)
    {
        var verdict = BrokerHeldPositionGate.Evaluate(
            CloseIntent(qty: orderQuantity), [Position(netQuantity)]);

        verdict.Outcome.Should().Be(expected);
        verdict.ClosableQuantity.Should().Be(closable, "決済方向の実際の建玉数（注文数量で切らない）");
        verdict.BrokerNetQuantity.Should().Be(netQuantity);
    }

    // 🔴 空列（建玉ゼロ）と null（不明）を取り違えない —— 倒す先も理由も違う。
    [Fact]
    public void 照会不能と建玉ゼロは別の結論になる()
    {
        BrokerHeldPositionGate.Evaluate(CloseIntent(qty: 10), null).Outcome
            .Should().Be(BrokerHeldPositionOutcome.Indeterminate);
        BrokerHeldPositionGate.Evaluate(CloseIntent(qty: 10), []).Outcome
            .Should().Be(BrokerHeldPositionOutcome.NoPosition);
    }

    // 同一銘柄・同一市場が複数行で返っても合算する（突合の単位は (銘柄, 市場)）。他の銘柄は数えない。
    [Fact]
    public void 同じ銘柄の複数行は合算し他の銘柄は数えない()
    {
        var snapshot = new List<BrokerPositionSnapshot>
        {
            Position(60),
            Position(40),
            new("MSFT", Market.UnitedStates, 500, 100m),
            new("AAPL", Market.Japan, 500, 100m),
        };

        var verdict = BrokerHeldPositionGate.Evaluate(CloseIntent(qty: 100), snapshot);

        verdict.Outcome.Should().Be(BrokerHeldPositionOutcome.Proceed);
        verdict.BrokerNetQuantity.Should().Be(100);
    }

    // 🔴 T-10-517（否定形・#873 の監査 N1）: **両建て（同一銘柄にロングとショートが同時にある）でも
    // 正当な決済を止めない。** ネット（符号付き合算）で数えると、ロング +300・ショート −100 のネットは +200 であり、
    // ショート 100 株の買い戻しが「建玉なし」で見送られ、ロング 300 株の売り決済も 200 株へ縮められる
    // ——**どちらも FR-10 に反する側の誤り**である。方向ごとに数えればどちらも全量が通る。
    [Fact]
    public async Task 両建ての銘柄でも決済方向の建玉で判定する()
    {
        // ブローカーはロング 300 株とショート 100 株を同時に持つ（ネットは +200）。
        var snapshot = new List<BrokerPositionSnapshot> { Position(300), Position(-100) };

        // (a) ロング 300 株の売り決済: **縮めない**（200 株へ縮めるのは誤り）。
        var longBroker = new FakePositionAwareBroker(snapshot);
        var (longService, _, _) = NewService(longBroker);
        var longResult = await longService.ExecuteAsync(Approved(CloseIntent(qty: 300)));

        longBroker.Placed.Should().ContainSingle().Which.Quantity.Should().Be(300);
        longResult.Drift.Should().BeNull();

        // (b) ショート 100 株の買い戻し: **送る**（「建玉なし」で見送るのは誤り）。
        var shortBroker = new FakePositionAwareBroker(snapshot);
        var (shortService, _, _) = NewService(shortBroker);
        var shortResult = await shortService.ExecuteAsync(
            Approved(CloseIntent(qty: 100, side: TradeSide.Buy)));

        shortBroker.Placed.Should().ContainSingle().Which.Quantity.Should().Be(100);
        shortResult.Forgone.Should().BeNull();
        shortResult.Drift.Should().BeNull();
    }

    // 🔴 T-10-519（否定形・#873 の監査 NB1）: **ネットが 0 でも「ブローカーには無い」と報告しない。**
    // 方向ごとに数えるようにした（N1）ことで closable と net は独立した —— ロング +100 / ショート −100 の
    // ネットは 0 だが、売りの決済に使えるロングは 100 株実在する。100 株を縮めて**送った直後に**
    // 「台帳にだけある建玉（ブローカーには無い）」と報告すると、通知の文面も監査台帳も誤る。
    [Fact]
    public async Task 両建てでネットが0でも縮めて送った乖離は台帳のみとは分類しない()
    {
        var broker = new FakePositionAwareBroker([Position(100), Position(-100)]); // ネット 0・ロングは 100 株
        var (service, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(CloseIntent(qty: 300)));

        broker.Placed.Should().ContainSingle().Which.Quantity.Should().Be(100, "実在するロング 100 株は送る");

        var drift = result.Drift!.Drifts.Should().ContainSingle().Subject;
        drift.Kind.Should().Be(
            PositionDriftKind.QuantityMismatch, "100 株を送った直後に『ブローカーには無い』と報告しない");
        drift.LedgerQuantity.Should().Be(300);
        drift.BrokerQuantity.Should().Be(0, "報告する数量はネットのまま（定期突合と同じ物差し）");
    }

    // T-10-519: 純関数の側でも分類を固定する（本当に 1 株も無いときだけ「台帳のみ」である）。
    [Fact]
    public void 台帳のみと分類するのは決済方向にもネットにも建玉が無いときだけである()
    {
        var intent = CloseIntent(qty: 300);

        BrokerHeldPositionGate.DriftOf(intent, brokerNetQuantity: 0, closableQuantity: 0)
            .Kind.Should().Be(PositionDriftKind.LedgerOnly);
        BrokerHeldPositionGate.DriftOf(intent, brokerNetQuantity: 0, closableQuantity: 100)
            .Kind.Should().Be(PositionDriftKind.QuantityMismatch, "両建てでネットだけが 0 になった場合");
        BrokerHeldPositionGate.DriftOf(intent, brokerNetQuantity: -100, closableQuantity: 0)
            .Kind.Should().Be(PositionDriftKind.QuantityMismatch, "反対方向の建玉しか無い場合");
    }

    // T-10-517: 純関数の側でも両建てを固定する（判定は方向ごと・報告はネット）。
    [Fact]
    public void 両建てでは判定は方向ごとで報告はネットである()
    {
        var snapshot = new List<BrokerPositionSnapshot> { Position(300), Position(-100) };

        var sell = BrokerHeldPositionGate.Evaluate(CloseIntent(qty: 300), snapshot);
        sell.Outcome.Should().Be(BrokerHeldPositionOutcome.Proceed);
        sell.ClosableQuantity.Should().Be(300, "売りの決済が消せるのはロングの 300 株である");
        sell.BrokerNetQuantity.Should().Be(200, "報告はネット（定期突合と同じ物差し）");

        var buy = BrokerHeldPositionGate.Evaluate(CloseIntent(qty: 100, side: TradeSide.Buy), snapshot);
        buy.Outcome.Should().Be(BrokerHeldPositionOutcome.Proceed);
        buy.ClosableQuantity.Should().Be(100, "買いの決済が消せるのはショートの 100 株である");

        // 🔴 #873 の監査（3 巡目）1: **注文数量より建玉が多くても、入るのは実際の建玉数である。**
        // 現在の読み手は Proceed の本項目を見ないが、分岐で意味が変わる値は次の追随で必ず踏む。
        var partial = BrokerHeldPositionGate.Evaluate(CloseIntent(qty: 100), snapshot);
        partial.Outcome.Should().Be(BrokerHeldPositionOutcome.Proceed);
        partial.ClosableQuantity.Should().Be(300, "注文が 100 株でもロングの実建玉は 300 株である");
    }
}
