using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-10, FR-12, UC-02, ADR-0040 決定1（S1）, #820 の 4 巡目監査, IADR-0344 追記(4):
// 4 巡目の監査が挙げたブロッキング 4 件（BLK-1〜4）の再発防止。
//
// 🔴 **本ファイルのテストは、作り直し前のコード（配分方式）でも「コンパイルできる」形に保つ**
// ——赤→緑（是正前に落ちること）を実測するためである。新しい列（残保護数量）を直接見るテストは
// SoftwareStopExecutorTests / ProtectiveStopGuardSoftwareStopTests / EfProtectiveStopOrderStoreTests 側に置く。
public class SoftwareStopBlockingRegressionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 15, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class FakeBroker : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = [];

        public Dictionary<string, BrokerOrder> Orders { get; } = new();

        public List<(OrderIntent Intent, Guid DecisionId)> MarketCloses { get; } = [];
        public List<string> Cancelled { get; } = [];

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            throw new NotSupportedException("S1 はブローカーへ逆指値を出さない");

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            MarketCloses.Add((closeIntent, decisionId));
            return Task.FromResult(new BrokerOrder(
                $"close-{MarketCloses.Count}", closeIntent, OrderStatus.Accepted, 0, 0m, Now, null));
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult(Orders.TryGetValue(orderId, out var order) ? order : null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            Cancelled.Add(orderId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult(Positions);
    }

    private sealed record Fixture(
        SoftwareStopExecutor Executor,
        ProtectiveStopGuard Guard,
        FakeBroker Broker,
        InMemoryProtectiveStopOrderStore Stops,
        InMemoryExecutedOrderStore Store);

    private static Fixture NewFixture()
    {
        var broker = new FakeBroker();
        var stops = new InMemoryProtectiveStopOrderStore();
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var clock = new FakeClock();
        var executor = new SoftwareStopExecutor(broker, broker, stops, store, reservations, clock);
        return new Fixture(
            executor, new ProtectiveStopGuard(broker, broker, stops, store, clock, executor), broker, stops, store);
    }

    private static BrokerPositionSnapshot Long(int qty) => new("AAPL", Market.UnitedStates, qty, 1_000m);

    private static ProtectiveStopOrder SoftwareStop(DateTimeOffset createdAt, int quantity = 10, DateTimeOffset? triggeredAt = null)
    {
        var id = Guid.NewGuid();
        return new ProtectiveStopOrder(
            id, ProtectiveStopIds.SoftwareStopId(id), string.Empty, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, 950m, 1m, 0, ProtectiveStopState.Active,
            createdAt, createdAt, StopLossExecutionMethod.SoftwareStop, triggeredAt, triggeredAt is null ? null : 940m);
    }

    private static ProtectiveStopOrder BrokerStop(
        DateTimeOffset createdAt, int quantity, string stopOrderId = "stop-s0",
        ProtectiveStopState state = ProtectiveStopState.Active)
    {
        var id = Guid.NewGuid();
        return new ProtectiveStopOrder(
            id, ProtectiveStopIds.StopDecisionId(id, 1), stopOrderId, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, 900m, 1m, 1, state, createdAt, createdAt);
    }

    private static void Entry(Fixture f, ProtectiveStopOrder stop, OrderStatus status, int filled) =>
        f.Store.Save(new ExecutionRecord(
            stop.EntryDecisionId, $"entry-{stop.EntryDecisionId:N}", "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, PositionEffect.Open, stop.Quantity, 1_000m, filled, 1_000m, status, 0m, Now.AddHours(-4)));

    // ---- BLK-3: 部分的にしか決済できていない行を完了させない ----

    // T-10-372（受け入れ基準 26）: 同じ試行の決済記録が**行の残保護数量より小さい数量**で残っている
    //（発注はできたがクラッシュで行の更新を落とした窓）。行を完了させると、決済できていない残りが
    // **無保護のまま黙って残る**。残保護数量を減らすだけにとどめ、残りを次の巡回で決済する。
    [Fact]
    public async Task 部分的にしか決済できない行は完了させず残りを次の巡回で決済する()
    {
        var f = NewFixture();
        var stop = SoftwareStop(Now.AddHours(-1), quantity: 10, triggeredAt: Now);
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);
        f.Broker.Positions = [Long(10)];

        // 試行 1 の決済は 6 株だけ受理されて記録されている（行の更新だけが失われた）。
        f.Store.Save(new ExecutionRecord(
            ProtectiveStopIds.SoftwareCloseDecisionId(stop.EntryDecisionId, 1), "close-partial", "AAPL",
            Market.UnitedStates, TradeSide.Sell, ProductType.Cash, PositionEffect.Close, 6, 940m, 0, 0m,
            OrderStatus.Accepted, 0m, Now));

        var first = await f.Executor.TryCloseAsync(f.Stops.Find(stop.EntryDecisionId)!, snapshot: null);

        first.Kind.Should().NotBe(SoftwareStopCloseKind.Completed, "4 株が決済できていない");
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(
            ProtectiveStopState.Active, "完了させると残り 4 株が無保護のまま黙って残る");
        f.Broker.MarketCloses.Should().BeEmpty("記録済みの試行に重ねて発注しない");

        // 次の巡回で残りの 4 株を決済して完了する。
        f.Broker.Positions = [Long(4)];
        var second = await f.Executor.TryCloseAsync(f.Stops.Find(stop.EntryDecisionId)!, snapshot: null);

        second.Kind.Should().Be(SoftwareStopCloseKind.Completed);
        f.Broker.MarketCloses.Should().ContainSingle().Which.Intent.Quantity.Should().Be(4);
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // ---- BLK-1: 自分の建玉を失った S1 行が、生きている S0 の逆指値を取り消させる ----

    // T-10-373（受け入れ基準 27）: 同じ銘柄に「建玉を失った S1 の行（古い）」と「別エントリーの生きた S0 逆指値（新しい）」が
    // 併存する。作り直し前は S1 行が方向の純額だけを見て**不死化**し、S0 側は S1 の主張ぶんを引いて残 0 と誤認するため、
    // **毎巡回・恒久的に生きた逆指値を黙って取り消していた**。
    // 残保護数量方式では、減った建玉を**古い行から一度だけ**割り当てるので、S1 行が 0 になって完了し、S0 は無傷で残る。
    [Fact]
    public async Task 建玉を失ったS1の行は完了し生きているS0の逆指値を取り消させない()
    {
        var f = NewFixture();
        var ghost = SoftwareStop(Now.AddHours(-3), quantity: 10); // 建玉は手動決済で消えている
        var alive = BrokerStop(Now.AddHours(-1), quantity: 5);    // 別エントリーの生きた S0 逆指値
        f.Stops.Save(ghost);
        f.Stops.Save(alive);
        Entry(f, ghost, OrderStatus.Filled, 10);
        f.Broker.Orders["stop-s0"] = new BrokerOrder(
            "stop-s0", new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                BrokerProvider.MoomooSimulate, 5, 900m, PositionEffect.Close), OrderStatus.Accepted, 0, 0m, Now, null);
        f.Broker.Positions = [Long(5)]; // 残っているのは S0 の 5 株だけ

        await f.Guard.RunOnceAsync(10);

        f.Broker.Cancelled.Should().BeEmpty("生きている S0 の逆指値を取り消してはならない（建玉 5 株を守っている）");
        f.Stops.Find(alive.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
        f.Stops.Find(ghost.EntryDecisionId)!.State.Should().Be(
            ProtectiveStopState.Completed, "建玉を失った行は不死化せず、一度の割り当てで役目を終える");

        // 次の巡回でも取り消さない（作り直し前は毎巡回・恒久的に取り消していた）。
        await f.Guard.RunOnceAsync(10);
        f.Broker.Cancelled.Should().BeEmpty();
    }

    // ---- BLK-2: 完了済み S0 行の取消済みレグ記録が S1 の持ち分を食う ----

    // T-10-374（受け入れ基準 28）: 作り直し前は「建玉照会がまだ映していない決済」を発注記録から数えており、
    // **完了した S0 行のレグ記録**（取り消された注文が当日一覧から消えるブローカーでは終端化できず Accepted のまま残る）が
    // 除外集合に入らず、S1 の持ち分を丸ごと削っていた（損切りが 1 株も出ない）。
    // 残保護数量方式は持ち分の計算に発注記録を**一切使わない**ため、この経路が構造的に消える。
    [Fact]
    public async Task 完了済みS0の取消済みレグ記録があってもS1の持ち分は削られない()
    {
        var f = NewFixture();
        var stop = SoftwareStop(Now.AddHours(-1), quantity: 10, triggeredAt: Now);
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);

        // 別エントリーの S0 は既に完了（建玉消滅で逆指値を取り消した）が、レグ記録は Accepted のまま残っている。
        var done = BrokerStop(Now.AddHours(-2), quantity: 10, stopOrderId: "stop-done", state: ProtectiveStopState.Completed);
        f.Stops.Save(done);
        f.Store.Save(new ExecutionRecord(
            done.StopDecisionId, "stop-done", "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            PositionEffect.Close, 10, 900m, 0, 0m, OrderStatus.Accepted, 0m, Now));

        f.Broker.Positions = [Long(10)]; // 残っているのは S1 の 10 株だけ

        var outcome = await f.Executor.TryCloseAsync(f.Stops.Find(stop.EntryDecisionId)!, snapshot: null);

        outcome.Kind.Should().Be(SoftwareStopCloseKind.Completed);
        f.Broker.MarketCloses.Should().ContainSingle()
            .Which.Intent.Quantity.Should().Be(10, "完了済み S0 のレグ記録は持ち分と無関係である");
    }

    // ---- BLK-4: 未到達の行が到達済みの行より先に持ち分を取る ----

    // T-10-375（受け入れ基準 29）: 同じ銘柄に「未到達の古い行」と「到達済みの新しい行」があり、建玉が両方を賄えない。
    // 作り直し前は配分が作成順だったため、**未到達の行が全量を取り、到達した行の決済が 1 株も出なかった**
    //（しかも据え置きのまま無音）。残保護数量方式では減少分を古い行から一度だけ割り当て、
    // ガードは**到達済みの行を未到達の行より先に**処理するため、到達した損切りが必ず出る。
    [Fact]
    public async Task 到達済みの行を未到達の行より先に処理する()
    {
        var f = NewFixture();
        var unreached = SoftwareStop(Now.AddHours(-3), quantity: 10);
        var reached = SoftwareStop(Now.AddHours(-1), quantity: 10, triggeredAt: Now);
        f.Stops.Save(unreached);
        f.Stops.Save(reached);
        Entry(f, unreached, OrderStatus.Filled, 10);
        Entry(f, reached, OrderStatus.Filled, 10);
        f.Broker.Positions = [Long(10)]; // 20 株のうち 10 株が外部で消えている

        await f.Guard.RunOnceAsync(10);

        f.Broker.MarketCloses.Should().ContainSingle("到達した行の損切りが出る")
            .Which.Intent.Quantity.Should().Be(10);
        f.Broker.MarketCloses[0].DecisionId.Should().Be(
            ProtectiveStopIds.SoftwareCloseDecisionId(reached.EntryDecisionId, 1));
        f.Stops.Find(reached.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }
}
