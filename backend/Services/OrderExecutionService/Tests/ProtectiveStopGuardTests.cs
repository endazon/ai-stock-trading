using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-10, UC-02, #331, IADR-0210 決定4: 保護逆指値ガード（失効検知・再発注・残存取消）の検証。
// 業務フロー 02「逆指値の未受理・失効を検知 → 再発注、不可なら成行で手仕舞い」の分岐表と、
// 「建玉なき逆指値を残さない」（反対建玉の防止）・「不明は据え置く」（fail-safe）を固定する。
public class ProtectiveStopGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 7, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class GuardBroker : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        /// <summary>StopOrderId → 照会結果（未登録は null＝照会不能）。</summary>
        public Dictionary<string, BrokerOrder?> Orders { get; } = new();

        /// <summary>建玉スナップショット（null＝照会不能）。</summary>
        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = [];

        public bool RejectReplacement { get; set; }
        public bool ThrowOnMarketClose { get; set; }

        /// <summary>この StopOrderId の注文照会は例外になる（ブローカ側の異常。その 1 件の評価が落ちる）。</summary>
        public HashSet<string> ThrowOnGetOrderIds { get; } = [];

        /// <summary>再発注の送信が例外になる（接続断など）。</summary>
        public bool ThrowOnStopPlace { get; set; }

        public List<string> Cancelled { get; } = [];
        public int StopPlaceCount { get; private set; }
        public int MarketCloseCount { get; private set; }
        public OrderIntent? LastStopIntent { get; private set; }
        public OrderIntent? LastCloseIntent { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException("ガードは通常発注を行わない");

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default)
        {
            StopPlaceCount++;
            LastStopIntent = closeIntent;
            if (ThrowOnStopPlace)
                throw new InvalidOperationException("再発注の送信に失敗（テスト）");
            return Task.FromResult(new BrokerOrder(
                $"stop-re-{StopPlaceCount}", closeIntent,
                RejectReplacement ? OrderStatus.Rejected : OrderStatus.Accepted, 0, 0m, Now,
                RejectReplacement ? Now : null));
        }

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            MarketCloseCount++;
            LastCloseIntent = closeIntent;
            return ThrowOnMarketClose
                ? throw new InvalidOperationException("成行手仕舞いに失敗（テスト）")
                : Task.FromResult(new BrokerOrder(
                    $"close-{MarketCloseCount}", closeIntent, OrderStatus.Filled,
                    closeIntent.Quantity, closeIntent.Price, Now, Now));
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            ThrowOnGetOrderIds.Contains(orderId)
                ? throw new InvalidOperationException($"注文照会に失敗（テスト）: {orderId}")
                : Task.FromResult(Orders.TryGetValue(orderId, out var order) ? order : null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            Cancelled.Add(orderId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult(Positions);
    }

    private static ProtectiveStopOrder ActiveStop(
        TradeSide entrySide = TradeSide.Buy, int quantity = 10, int attempt = 1, string stopOrderId = "stop-1") =>
        new(Guid.NewGuid(), Guid.NewGuid(), stopOrderId, "AAPL", Market.UnitedStates, entrySide,
            ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, 950m, 1m, attempt,
            ProtectiveStopState.Active, Now.AddMinutes(-5), Now.AddMinutes(-5));

    private static BrokerOrder StopOrder(OrderStatus status) =>
        new("stop-1", new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 10, 950m, PositionEffect.Close), status, 0, 0m, Now,
            OrderStatusLifecycle.IsTerminal(status) ? Now : null);

    private static (ProtectiveStopGuard Guard, GuardBroker Broker, InMemoryProtectiveStopOrderStore Stops,
        InMemoryExecutedOrderStore Store) NewGuard(ProtectiveStopOrder stop)
    {
        var broker = new GuardBroker();
        var stops = new InMemoryProtectiveStopOrderStore();
        stops.Save(stop);
        var store = new InMemoryExecutedOrderStore();
        return (new ProtectiveStopGuard(broker, broker, stops, store, new FakeClock()), broker, stops, store);
    }

    private static BrokerPositionSnapshot Long(int qty) => new("AAPL", Market.UnitedStates, qty, 1_000m);
    private static BrokerPositionSnapshot Short(int qty) => new("AAPL", Market.UnitedStates, -qty, 1_000m);

    // ---- 分岐表（境界値テーブル） ----

    [Fact]
    public async Task 建玉あり_逆指値滞留中は何もしない()
    {
        var stop = ActiveStop();
        var (guard, broker, stops, _) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Accepted);
        broker.Positions = [Long(10)];

        var result = await guard.RunOnceAsync(10);

        result.StillActive.Should().Be(1);
        broker.Cancelled.Should().BeEmpty();
        broker.StopPlaceCount.Should().Be(0);
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }

    [Fact]
    public async Task 建玉が消滅したら残存逆指値を取り消す()
    {
        // 決済済みの建玉に残る逆指値が発火すると反対方向の建玉を生む（業務フロー 02 補足）。
        var stop = ActiveStop();
        var (guard, broker, stops, _) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Accepted);
        broker.Positions = []; // 建玉なし

        var result = await guard.RunOnceAsync(10);

        broker.Cancelled.Should().ContainSingle().Which.Should().Be("stop-1");
        result.Completed.Should().Be(1);
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    [Fact]
    public async Task 逆指値が約定したら保護完了として記録する()
    {
        var stop = ActiveStop();
        var (guard, broker, stops, _) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Filled);
        broker.Positions = [];

        var result = await guard.RunOnceAsync(10);

        result.Completed.Should().Be(1);
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        broker.StopPlaceCount.Should().Be(0);
        broker.MarketCloseCount.Should().Be(0);
    }

    [Theory]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Rejected)]
    [InlineData(OrderStatus.Expired)]
    public async Task 失効した逆指値は建玉が残っていれば再発注する(OrderStatus lapsed)
    {
        var stop = ActiveStop();
        var (guard, broker, stops, store) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(lapsed);
        broker.Positions = [Long(10)];

        var result = await guard.RunOnceAsync(10);

        result.Replaced.Should().Be(1);
        broker.StopPlaceCount.Should().Be(1);
        var updated = stops.Find(stop.EntryDecisionId)!;
        updated.State.Should().Be(ProtectiveStopState.Active);
        updated.Attempt.Should().Be(2);
        updated.StopOrderId.Should().Be("stop-re-1");
        // 再発注は決定的な新 DecisionId（試行番号つき）で冪等化され、レグは発注結果として記録される。
        updated.StopDecisionId.Should().Be(ProtectiveStopIds.StopDecisionId(stop.EntryDecisionId, 2));
        store.FindByDecisionId(updated.StopDecisionId).Should().NotBeNull();
        var placed = result.Events.OfType<ProtectiveStopPlaced>().Should().ContainSingle().Which;
        placed.Attempt.Should().Be(2);
    }

    [Fact]
    public async Task 再発注は残存建玉の数量に切り詰める()
    {
        // 部分決済済みの建玉（残 3）に元数量 10 の逆指値を張り直すと過剰決済＝反対建玉のリスクになる。
        var stop = ActiveStop(quantity: 10);
        var (guard, broker, _, _) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Cancelled);
        broker.Positions = [Long(3)];

        await guard.RunOnceAsync(10);

        broker.LastStopIntent!.Quantity.Should().Be(3);
    }

    [Fact]
    public async Task 再発注できなければ成行で手仕舞う()
    {
        var stop = ActiveStop();
        var (guard, broker, stops, store) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Cancelled);
        broker.Positions = [Long(10)];
        broker.RejectReplacement = true;

        var result = await guard.RunOnceAsync(10);

        result.ClosedOut.Should().Be(1);
        broker.MarketCloseCount.Should().Be(1);
        broker.LastCloseIntent!.Side.Should().Be(TradeSide.Sell);
        var lost = result.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle().Which;
        lost.Cause.Should().Be(ProtectiveStopLossCause.LapsedInFlight);
        lost.Remediation.Should().Be(ProtectiveStopRemediation.PositionClosed);
        lost.CloseDecisionId.Should().NotBeNull();
        store.FindByDecisionId(lost.CloseDecisionId!.Value).Should().NotBeNull();
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    [Fact]
    public async Task 手仕舞いも失敗したらNoneで人手対応を求め記録はActiveのまま残す()
    {
        var stop = ActiveStop();
        var (guard, broker, stops, _) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Cancelled);
        broker.Positions = [Long(10)];
        broker.RejectReplacement = true;
        broker.ThrowOnMarketClose = true;

        var result = await guard.RunOnceAsync(10);

        var lost = result.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle().Which;
        lost.Remediation.Should().Be(ProtectiveStopRemediation.None);
        // 次回巡回で再試行できるよう Active のまま残す（黙って完了にしない）。
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }

    [Fact]
    public async Task 失効かつ建玉なしは完了として記録する()
    {
        var stop = ActiveStop();
        var (guard, broker, stops, _) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Cancelled);
        broker.Positions = [];

        var result = await guard.RunOnceAsync(10);

        result.Completed.Should().Be(1);
        broker.StopPlaceCount.Should().Be(0);
        broker.MarketCloseCount.Should().Be(0);
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // ---- fail-safe（否定形: 不明を「無い」と取り違えない） ----

    [Fact]
    public async Task 注文照会が不能なら据え置く_否定形()
    {
        var stop = ActiveStop();
        var (guard, broker, stops, _) = NewGuard(stop);
        // Orders 未登録 = 照会不能（null）。
        broker.Positions = [];

        var result = await guard.RunOnceAsync(10);

        result.Unknown.Should().Be(1);
        broker.Cancelled.Should().BeEmpty("状態不明の逆指値を取り消してはならない");
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }

    [Fact]
    public async Task 建玉照会が不能なら巡回ごと据え置く_否定形()
    {
        var stop = ActiveStop();
        var (guard, broker, stops, _) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Accepted);
        broker.Positions = null; // 照会不能

        var result = await guard.RunOnceAsync(10);

        result.Unknown.Should().Be(1);
        broker.Cancelled.Should().BeEmpty("建玉不明のまま「消滅した」と誤認して逆指値を消してはならない");
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }

    // ---- 方向（ショート）と数量の符号 ----

    [Fact]
    public async Task ショート建玉の残はマイナス数量から求める()
    {
        var stop = ActiveStop(entrySide: TradeSide.Sell);
        var (guard, broker, _, _) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Cancelled);
        broker.Positions = [Short(10)];

        var result = await guard.RunOnceAsync(10);

        result.Replaced.Should().Be(1, "ショート建玉（数量 −10）が残っている＝保護が要る");
        broker.LastStopIntent!.Side.Should().Be(TradeSide.Buy, "ショートの決済は買戻し");
    }

    [Fact]
    public void 残数量の算定はエントリー方向に一致する建玉だけを数える()
    {
        var longStop = ActiveStop(entrySide: TradeSide.Buy);
        var shortStop = ActiveStop(entrySide: TradeSide.Sell);

        // 境界値: ロング残 0 ↔ 1、ショート残 0 ↔ 1、反対方向は 0 と数える。
        ProtectiveStopGuard.RemainingPositionFor(longStop, [Long(1)]).Should().Be(1);
        ProtectiveStopGuard.RemainingPositionFor(longStop, []).Should().Be(0);
        ProtectiveStopGuard.RemainingPositionFor(longStop, [Short(5)]).Should().Be(0);
        ProtectiveStopGuard.RemainingPositionFor(shortStop, [Short(1)]).Should().Be(1);
        ProtectiveStopGuard.RemainingPositionFor(shortStop, [Long(5)]).Should().Be(0);
        ProtectiveStopGuard.RemainingPositionFor(shortStop, []).Should().Be(0);
    }

    // ---- バッチの fail-safe（1 件の異常で巡回全体を止めない） ----

    [Fact]
    public async Task 巡回対象が無ければ建玉照会もしない_否定形()
    {
        // 保護中の建玉がゼロのときに毎回 OpenD へ建玉照会を投げると、無駄な往復が常時走る。
        // 「対象ゼロなら何もしない」を固定する（結果は空のサマリ）。
        var broker = new GuardBroker { Positions = null };
        var guard = new ProtectiveStopGuard(
            broker, broker, new InMemoryProtectiveStopOrderStore(), new InMemoryExecutedOrderStore(), new FakeClock());

        var result = await guard.RunOnceAsync(10);

        result.Should().Be(ProtectiveStopGuardResult.Empty);
        result.Scanned.Should().Be(0);
        result.Failed.Should().Be(0);
        result.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task 一件の評価が例外でも残りの件は処理し失敗として数える()
    {
        // 1 銘柄のブローカ照会が壊れたとき、巡回全体が中断すると**他の建玉の保護喪失が検知されなくなる**。
        // 失敗は件数へ計上し（可観測性）、残りの評価は続ける。
        var broken = ActiveStop(stopOrderId: "stop-broken");
        var healthy = ActiveStop(stopOrderId: "stop-ok");
        var broker = new GuardBroker
        {
            Positions = [Long(10)],
        };
        broker.ThrowOnGetOrderIds.Add("stop-broken");
        broker.Orders["stop-ok"] = StopOrder(OrderStatus.Accepted);
        var stops = new InMemoryProtectiveStopOrderStore();
        stops.Save(broken);
        stops.Save(healthy);
        var guard = new ProtectiveStopGuard(
            broker, broker, stops, new InMemoryExecutedOrderStore(), new FakeClock());

        var result = await guard.RunOnceAsync(10);

        result.Scanned.Should().Be(2);
        result.Failed.Should().Be(1, "壊れた 1 件だけを失敗として数える");
        result.StillActive.Should().Be(1, "残りの 1 件は評価され続ける");
    }

    [Fact]
    public async Task 再発注の送信が例外でも手仕舞いへ倒れる()
    {
        // 「再発注の**拒否**」だけでなく「再発注の**送信失敗**（接続断など）」も、逆指値なしの建玉を
        // 残さない側へ倒れなければならない。例外がそのまま抜けると建玉が無防備なまま残る。
        var stop = ActiveStop();
        var (guard, broker, stops, store) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Cancelled);
        broker.Positions = [Long(10)];
        broker.ThrowOnStopPlace = true;

        var result = await guard.RunOnceAsync(10);

        result.ClosedOut.Should().Be(1);
        broker.MarketCloseCount.Should().Be(1, "再発注が送れなければ成行で手仕舞う");
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        store.FindByDecisionId(ProtectiveStopIds.CloseDecisionId(stop.EntryDecisionId, attempt: 2))
            .Should().NotBeNull("手仕舞いレグは約定追跡へ載る");
        result.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle(e =>
            e.Remediation == ProtectiveStopRemediation.PositionClosed);
    }
}
