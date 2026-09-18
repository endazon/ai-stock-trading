using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-10, ADR-0040 決定1（S1）, #820（#826 項目 3）, IADR-0344 決定6: 保護逆指値ガードとソフトウェア逆指値。
// 受け入れ基準 6（到達済みの再試行）・7（ブローカー照会をしない・建玉消滅で解消）・8（手法混在の按分）を固定する。
public class ProtectiveStopGuardSoftwareStopTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 16, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class GuardBroker : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = [];
        public Dictionary<string, BrokerOrder> Orders { get; } = new();

        public List<string> OrderQueries { get; } = [];
        public List<string> Cancelled { get; } = [];
        public int MarketCloseCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            throw new NotSupportedException("S1 の行に逆指値を再発注してはならない");

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            MarketCloseCount++;
            return Task.FromResult(new BrokerOrder(
                $"close-{MarketCloseCount}", closeIntent, OrderStatus.Accepted, 0, 0m, Now, null));
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default)
        {
            OrderQueries.Add(orderId);
            return Task.FromResult(Orders.TryGetValue(orderId, out var order) ? order : null);
        }

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            Cancelled.Add(orderId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult(Positions);
    }

    private sealed record Fixture(
        ProtectiveStopGuard Guard, GuardBroker Broker, InMemoryProtectiveStopOrderStore Stops, InMemoryExecutedOrderStore Store);

    private static Fixture NewFixture()
    {
        var broker = new GuardBroker();
        var stops = new InMemoryProtectiveStopOrderStore();
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var clock = new FakeClock();
        var executor = new SoftwareStopExecutor(broker, broker, stops, store, reservations, clock);
        return new Fixture(new ProtectiveStopGuard(broker, broker, stops, store, clock, executor), broker, stops, store);
    }

    private static BrokerPositionSnapshot Long(int qty) => new("AAPL", Market.UnitedStates, qty, 1_000m);

    private static ProtectiveStopOrder SoftwareStop(DateTimeOffset? triggeredAt = null)
    {
        var id = Guid.NewGuid();
        return new ProtectiveStopOrder(
            id, ProtectiveStopIds.SoftwareStopId(id), string.Empty, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, 0, ProtectiveStopState.Active,
            Now.AddHours(-1), Now.AddHours(-1), StopLossExecutionMethod.SoftwareStop,
            triggeredAt, triggeredAt is null ? null : 940m);
    }

    private static ProtectiveStopOrder BrokerStop(int quantity, string stopOrderId = "stop-s0")
    {
        var id = Guid.NewGuid();
        return new ProtectiveStopOrder(
            id, ProtectiveStopIds.StopDecisionId(id, 1), stopOrderId, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, 900m, 1m, 1, ProtectiveStopState.Active,
            Now.AddHours(-2), Now.AddHours(-2));
    }

    private static void EntryRecord(Fixture f, ProtectiveStopOrder stop, OrderStatus status, int filled) =>
        f.Store.Save(new ExecutionRecord(
            stop.EntryDecisionId, $"entry-{stop.EntryDecisionId:N}", "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, PositionEffect.Open, stop.Quantity, 1_000m, filled, 1_000m, status, 0m, Now.AddHours(-1)));

    [Fact]
    public async Task 未到達のソフトウェア逆指値はブローカーの注文照会をせず建玉があれば維持する()
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        EntryRecord(f, stop, OrderStatus.Filled, 10);
        f.Broker.Positions = [Long(10)];

        var result = await f.Guard.RunOnceAsync(10);

        result.StillActive.Should().Be(1);
        f.Broker.OrderQueries.Should().BeEmpty("ブローカーにソフトウェア逆指値の注文は無い");
        f.Broker.Cancelled.Should().BeEmpty();
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }

    [Fact]
    public async Task 建玉が消えたソフトウェア逆指値は完了し何も発注しない()
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        EntryRecord(f, stop, OrderStatus.Filled, 10);
        f.Broker.Positions = []; // 手動決済済み

        // #820 の 5 巡目監査, IADR-0344 追記(5): 削りの確定には 2 巡回の観測が要る
        //（建玉照会は 1 巡回だけ過少に返り得るため、1 回の観測で行を閉じない）。
        var first = await f.Guard.RunOnceAsync(10);
        first.StillActive.Should().Be(1);
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);

        var result = await f.Guard.RunOnceAsync(10);

        result.Completed.Should().Be(1);
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        f.Broker.OrderQueries.Should().BeEmpty();
        f.Broker.MarketCloseCount.Should().Be(0);
        f.Broker.Cancelled.Should().BeEmpty();
    }

    [Fact]
    public async Task 約定0で終端したエントリーのソフトウェア逆指値は完了する()
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        EntryRecord(f, stop, OrderStatus.Expired, 0); // 当日限りで失効

        await f.Guard.RunOnceAsync(10);

        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    [Theory]
    [InlineData(OrderStatus.Accepted)]
    [InlineData(OrderStatus.PartiallyFilled)]
    public async Task エントリーが未終端なら建玉が0でもソフトウェア逆指値を完了しない(OrderStatus status)
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        EntryRecord(f, stop, status, 0);
        f.Broker.Positions = [];

        var result = await f.Guard.RunOnceAsync(10);

        result.StillActive.Should().Be(1, "これから約定し得るエントリーの保護を外さない");
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }

    [Fact]
    public async Task 到達済みで決済できていないソフトウェア逆指値はガードが決済を再試行する()
    {
        // 再起動耐性: 到達は記録済み（発注執行の停止・接続断で決済が据え置かれた）。次の到達を待たずに決済する。
        var f = NewFixture();
        var stop = SoftwareStop(triggeredAt: Now.AddMinutes(-3));
        f.Stops.Save(stop);
        EntryRecord(f, stop, OrderStatus.Filled, 10);
        f.Broker.Positions = [Long(10)];

        var result = await f.Guard.RunOnceAsync(10);

        f.Broker.MarketCloseCount.Should().Be(1);
        result.ClosedOut.Should().Be(1);
        result.Events.OfType<SoftwareStopExecuted>().Should().ContainSingle()
            .Which.Outcome.Should().Be(SoftwareStopOutcome.ClosePlaced);
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);

        // 次の巡回では何もしない（二重決済なし）。
        await f.Guard.RunOnceAsync(10);
        f.Broker.MarketCloseCount.Should().Be(1);
    }

    // #826 項目 3 の S1 側: 建玉が保護記録の主張に足りなければ、超過分を削る。
    //
    // 🔴 **［2026-09-18 / #820 の 5 巡目監査］削る順は「帳簿だけの行（S1）→ 実注文を持つ行（S0）」である**
    // （IADR-0344 追記(5)）。本テストは以前「S0 の 5 株だけが外部で消えた」という**出自の仮定**を置いて
    // S0 の取消を期待していたが、**ブローカーの純額は建玉の出自を区別しない**——同じ照会結果は
    // 「S1 の 5 株が消えた」とも読める。生きた逆指値の取消は無音かつ不可逆であるため、
    // **超過は先に帳簿だけの行から削り、S0 は S1 で吸収しきれない分だけ削る**（下の T-10-383）。
    [Fact]
    public async Task 超過は帳簿だけの行から削り生きたS0の逆指値は取り消さない()
    {
        var f = NewFixture();
        var s0 = BrokerStop(quantity: 5);
        var s1 = SoftwareStop();
        f.Stops.Save(s0);
        f.Stops.Save(s1);
        EntryRecord(f, s1, OrderStatus.Filled, 10);
        f.Broker.Orders["stop-s0"] = new BrokerOrder(
            "stop-s0", new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                BrokerProvider.MoomooSimulate, 5, 900m, PositionEffect.Close), OrderStatus.Accepted, 0, 0m, Now, null);
        f.Broker.Positions = [Long(10)]; // 主張は 15 株・建玉は 10 株（どちらの 5 株が消えたかは分からない）

        await f.Guard.RunOnceAsync(10);
        await f.Guard.RunOnceAsync(10);

        f.Broker.Cancelled.Should().BeEmpty("実注文を持つ行は最後に回す（可逆な帳簿の削りを先に使う）");
        f.Stops.Find(s0.EntryDecisionId)!.ProtectedQuantity.Should().Be(5);
        f.Stops.Find(s1.EntryDecisionId)!.RemainingProtected.Should().Be(5, "超過 5 株は帳簿だけの行から削る");
        f.Stops.Find(s1.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active, "建玉は残っている");
    }

    // T-10-383（受け入れ基準 37）: S1 が超過を吸収しきれない構成では、**従来どおり S0 の逆指値を取り消す**
    //（#826 項目 3 は保たれる。建玉なき逆指値は発火すると反対建玉を生む）。確定には 2 巡回の観測が要る。
    [Fact]
    public async Task 建玉が丸ごと消えたらS1を削り切ったうえでS0の逆指値も取り消す()
    {
        var f = NewFixture();
        var s0 = BrokerStop(quantity: 5);
        var s1 = SoftwareStop();
        f.Stops.Save(s0);
        f.Stops.Save(s1);
        EntryRecord(f, s1, OrderStatus.Filled, 10);
        f.Broker.Orders["stop-s0"] = new BrokerOrder(
            "stop-s0", new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                BrokerProvider.MoomooSimulate, 5, 900m, PositionEffect.Close), OrderStatus.Accepted, 0, 0m, Now, null);
        f.Broker.Positions = []; // 建玉が丸ごと消えた（S1 の 10 株では超過 15 株を吸収しきれない）

        await f.Guard.RunOnceAsync(10);
        f.Broker.Cancelled.Should().BeEmpty("観測 1 回では確定しない");

        await f.Guard.RunOnceAsync(10);

        f.Broker.Cancelled.Should().ContainSingle().Which.Should().Be("stop-s0",
            "建玉なき逆指値を残すと、発火して反対建玉を生む");
        f.Stops.Find(s0.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        f.Stops.Find(s1.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // T-10-368: #820 の 3 巡目監査（B3）。手法間の差し引きは**対称**なので、S0 行と S1 行が同数を主張すると
    // どちらから見ても残 0 になる。S0 の逆指値が約定して建玉が S1 の分だけ残った巡回がまさにその形で、
    // ここで S1 行まで完了させると**建玉が残っているのに保護がゼロ**になる（イベントも Critical も出ない）。
    [Fact]
    public async Task S0の逆指値が約定した巡回でもS1の行は建玉が残る限り完了しない()
    {
        var f = NewFixture();
        var s0 = BrokerStop(quantity: 10);
        var s1 = SoftwareStop();
        f.Stops.Save(s0);
        f.Stops.Save(s1);
        EntryRecord(f, s1, OrderStatus.Filled, 10);
        // S0 の逆指値が正常に発動して約定（S0 の標準的な終わり方）。建玉は 20 → 10（残りは S1 の分）。
        f.Broker.Orders["stop-s0"] = new BrokerOrder(
            "stop-s0", new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                BrokerProvider.MoomooSimulate, 10, 900m, PositionEffect.Close), OrderStatus.Filled, 10, 900m, Now, Now);
        f.Broker.Positions = [Long(10)];

        await f.Guard.RunOnceAsync(10);

        f.Stops.Find(s0.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed, "S0 は約定で役目を終える");
        f.Stops.Find(s1.EntryDecisionId)!.State.Should().Be(
            ProtectiveStopState.Active, "10 株の建玉が残っている以上、S1 の保護を外してはならない");

        // #820 の 7 巡目監査, IADR-0344 追記(7): S0 の行が役目を終えて主張が消えた。
        // 削りは**未確定のまま帳簿へ書かれていない**ので、戻す（復元する）ものが無く、残保護数量は 10 のままである
        //（追記(5) はここで「復元する」と言っていたが、復元という操作そのものを撤去した）。
        await f.Guard.RunOnceAsync(10);

        var restored = f.Stops.Find(s1.EntryDecisionId)!;
        restored.State.Should().Be(ProtectiveStopState.Active);
        restored.RemainingProtected.Should().Be(10, "建玉 10 株を覆い続ける");
    }

    // T-10-369: 同上（B3）の配置だが、S0 の逆指値が**まだ生きている**場合。
    // 🔴 **［2026-09-18 / #820 の 5 巡目監査］期待値を改めた。** 「S0 の建玉だけが手で決済された」という読みと
    // 「S1 の建玉だけが消えた」という読みは、**ブローカーの純額からは区別できない同一の観測**である
    //（5 巡目の監査は、この形の作成時刻を入れ替えるだけで「生きた逆指値が取り消される」を再現した）。
    // 生きた注文の取消は**無音かつ不可逆**、帳簿の削りは**2 巡回の確認を経て通知される**ため、
    // 超過は帳簿だけの行から削る（IADR-0344 追記(5)）。**合計の主張は純額を超えないので反対建玉は作らない。**
    [Fact]
    public async Task S0の逆指値が生きている巡回では超過を帳簿だけの行から削る()
    {
        var f = NewFixture();
        var s0 = BrokerStop(quantity: 10);
        var s1 = SoftwareStop();
        f.Stops.Save(s0);
        f.Stops.Save(s1);
        EntryRecord(f, s1, OrderStatus.Filled, 10);
        f.Broker.Orders["stop-s0"] = new BrokerOrder(
            "stop-s0", new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                BrokerProvider.MoomooSimulate, 10, 900m, PositionEffect.Close), OrderStatus.Accepted, 0, 0m, Now, null);
        f.Broker.Positions = [Long(10)]; // 主張は 20 株・建玉は 10 株

        await f.Guard.RunOnceAsync(10);
        await f.Guard.RunOnceAsync(10);

        // ［2026-09-18 / #820 の 5 巡目監査］以前はここで S0 の取消を期待していたが、**どちらの 10 株が消えたかは
        // 純額からは分からない**。超過は先に帳簿だけの行（S1）から削り、生きた逆指値は取り消さない（IADR-0344 追記(5)）。
        f.Broker.Cancelled.Should().BeEmpty("生きた逆指値の取消は無音かつ不可逆であり、最後の手段に回す");
        f.Stops.Find(s0.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
        f.Stops.Find(s1.EntryDecisionId)!.State.Should().Be(
            ProtectiveStopState.Completed, "帳簿だけの行が超過を吸収して役目を終える（合計の主張は純額を超えない）");
    }

    [Fact]
    public async Task S1が無ければS0の建玉残の判定は従来と同一()
    {
        var f = NewFixture();
        var s0 = BrokerStop(quantity: 5);
        f.Stops.Save(s0);
        f.Broker.Orders["stop-s0"] = new BrokerOrder(
            "stop-s0", new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                BrokerProvider.MoomooSimulate, 5, 900m, PositionEffect.Close), OrderStatus.Accepted, 0, 0m, Now, null);
        f.Broker.Positions = [Long(3)];

        var result = await f.Guard.RunOnceAsync(10);

        result.StillActive.Should().Be(1);
        f.Broker.Cancelled.Should().BeEmpty();
    }
}
