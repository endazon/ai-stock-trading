using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.AdoptPositionDrift;
using OrderExecutionService.Features.OrderExecution.AmendOrder;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-641〜T-10-645, FR-10, FR-05, FR-11, UC-02, UC-06, #858, IADR-0370, IADR-0350 決定5:
// **利用者が承認した乖離の取り込みに、発注執行側の保護記録とブローカー側の保護注文を追随させる。**
//
// 取り込み（#849）は取引台帳だけを観測値へ合わせるため、保護記録は何も知らされないまま残っていた。
// 🔴 **建玉が無いのに売りの逆指値がブローカーに残ると、発火したとき意図しないショートが建つ**（#853 と同じ帰結）。
//
// 本クラスは「消えた建玉の保護を取り消して終端化する」「取り消せたと確認できなければ黙って閉じない」
// 「無関係な銘柄に触らない」「再送で二重に取り消さない」「保護を消しすぎない」を固定する。
public class ProtectiveStopDriftAdopterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 7, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // 取消の送信後に照会が返す状態を注入できるブローカー（moomoo の実挙動を模す。OrderCancellationConfirmationTests と同型）。
    private sealed class ScriptedBroker(OrderStatus? afterCancel = OrderStatus.Cancelled, bool cancelThrows = false)
        : IBrokerAdapter, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public int CancelCount { get; private set; }

        public List<string> CancelledOrderIds { get; } = [];

        /// <summary>建玉照会の応答（null＝照会不能＝不明）。</summary>
        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = [];

        /// <summary>建玉照会が例外で落ちる（OpenD の応答異常など）。</summary>
        public bool PositionsThrow { get; set; }

        /// <summary>取消を 1 本送った時点で呼ばれる（停止要求の注入に使う）。</summary>
        public Action? OnCancelSent { get; set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException("本テストは発注しない");

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(afterCancel is { } status
                ? new BrokerOrder(orderId, CloseIntent(), status, 0, 0m, Now, Now)
                : null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            CancelCount++;
            CancelledOrderIds.Add(orderId);
            OnCancelSent?.Invoke();
            return cancelThrows ? throw new InvalidOperationException("取消に失敗（テスト）") : Task.CompletedTask;
        }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            PositionsThrow
                ? throw new InvalidOperationException("建玉照会に失敗（テスト）")
                : Task.FromResult(Positions);
    }

    private static OrderIntent CloseIntent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
            10, 950m, PositionEffect.Close);

    private sealed record Harness(
        ProtectiveStopDriftAdopter Adopter,
        ScriptedBroker Broker,
        InMemoryProtectiveStopOrderStore Stops,
        InMemoryExecutedOrderStore Store);

    private static Harness NewHarness(ScriptedBroker? broker = null, bool withPositionSource = true)
    {
        broker ??= new ScriptedBroker();
        var stops = new InMemoryProtectiveStopOrderStore();
        var store = new InMemoryExecutedOrderStore();
        var amendments = new OrderAmendmentService(
            broker, store, new InMemoryOrderLifecycleStore(), new FakeClock());
        var adopter = new ProtectiveStopDriftAdopter(
            stops, amendments, new FakeClock(), logger: null, positions: withPositionSource ? broker : null);
        return new Harness(adopter, broker, stops, store);
    }

    /// <summary>ブローカー側逆指値（S0）の行と、その発注記録（取消は DecisionId から注文 ID を引く）。</summary>
    private static ProtectiveStopOrder AddBrokerStop(
        Harness h, string symbol = "AAPL", Market market = Market.UnitedStates,
        TradeSide entrySide = TradeSide.Buy, int quantity = 10, string orderId = "stop-1")
    {
        var entryDecisionId = Guid.NewGuid();
        var stopDecisionId = ProtectiveStopIds.StopDecisionId(entryDecisionId, attempt: 1);
        var stop = new ProtectiveStopOrder(
            entryDecisionId, stopDecisionId, orderId, symbol, market, entrySide, ProductType.Cash,
            BrokerProvider.MoomooSimulate, quantity, 950m, 1m, Attempt: 1, ProtectiveStopState.Active,
            Now.AddMinutes(-30), Now.AddMinutes(-30), RemainingProtected: quantity);
        h.Stops.Save(stop);
        h.Store.Save(new ExecutionRecord(
            stopDecisionId, orderId, symbol, market, entrySide == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy,
            ProductType.Cash, PositionEffect.Close, quantity, 950m, 0, 0m, OrderStatus.Accepted, 0m, Now.AddMinutes(-30)));
        return stop;
    }

    /// <summary>ソフトウェア逆指値（S1）の行（ブローカーに注文が無い＝帳簿だけの行）。</summary>
    private static ProtectiveStopOrder AddSoftwareStop(Harness h, int quantity = 4)
    {
        var entryDecisionId = Guid.NewGuid();
        var stop = new ProtectiveStopOrder(
            entryDecisionId, Guid.NewGuid(), string.Empty, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, 950m, 1m, Attempt: 0,
            ProtectiveStopState.Active, Now.AddMinutes(-20), Now.AddMinutes(-20),
            Mechanism: StopLossExecutionMethod.SoftwareStop, RemainingProtected: quantity);
        h.Stops.Save(stop);
        return stop;
    }

    private static PositionDriftAdopted Adopted(
        int before = 10, int after = 0, string symbol = "AAPL", Market market = Market.UnitedStates) =>
        new(Guid.NewGuid(), symbol, market, before, after, after, Now.AddMinutes(-5), 1_000m,
            RealizedPnlRecorded: false, ReferencePrice: null, EstimatedPnlInBase: null,
            Actor: "owner", Reason: "証券会社のアプリで全株売却", AdoptedAt: Now);

    // ---- T-10-641: 消えた建玉の保護レグを取り消して終端化する ----

    [Fact]
    public async Task 取り込みで建玉が消えたら_保護レグを取り消して記録を終端化し監査に残す()
    {
        var h = NewHarness();
        var stop = AddBrokerStop(h);

        var result = await h.Adopter.ApplyAsync(Adopted());

        h.Broker.CancelCount.Should().Be(1, "建玉が無いのに残る逆指値は、発火すると意図しないショートを建てる");
        h.Broker.CancelledOrderIds.Should().Equal("stop-1");
        result.Reduced.Should().Be(1);
        result.CancelUnconfirmed.Should().Be(0);

        var current = h.Stops.Find(stop.EntryDecisionId)!;
        current.State.Should().Be(ProtectiveStopState.Completed);
        current.RemainingProtected.Should().Be(0);

        // 取り消せたと**確認できた**ので在庫の押さえを解く（保護レグの承認行は台帳に残っている）。
        var cancelled = result.Events.OfType<OrderCancelled>().Should().ContainSingle().Which;
        cancelled.DecisionId.Should().Be(stop.StopDecisionId);
        cancelled.Reason.Should().Contain("乖離の取り込み").And.Contain("owner");

        // 保護が減ったことは監査・通知へ出す（無音の不可逆動作を残さない）。
        var reduced = result.Events.OfType<SoftwareStopExecuted>().Should().ContainSingle().Which;
        reduced.Outcome.Should().Be(SoftwareStopOutcome.ProtectionReduced);
        reduced.Quantity.Should().Be(10);
    }

    // ---- T-10-642: 取り消せたと確認できなければ黙って閉じない ----

    [Theory]
    [InlineData(null, false)]                      // 取消は送れたが照会が不明（null）
    [InlineData(OrderStatus.Accepted, false)]      // まだ終端でない（取消進行中に約定し得る）
    [InlineData(OrderStatus.Cancelled, true)]      // 取消の送信そのものが失敗した
    public async Task 保護レグを取り消せたと確認できなければ_記録はActiveのままでCriticalを出す_否定形(
        OrderStatus? afterCancel, bool cancelThrows)
    {
        var h = NewHarness(new ScriptedBroker(afterCancel, cancelThrows));
        var stop = AddBrokerStop(h);

        var result = await h.Adopter.ApplyAsync(Adopted());

        result.CancelUnconfirmed.Should().Be(1);
        result.Reduced.Should().Be(0);
        h.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active,
            "取り消せたと確認できていない逆指値の記録を閉じると、生きた注文が誰の巡回からも外れる");
        result.Events.OfType<OrderCancelled>().Should().BeEmpty("確認できないまま在庫の押さえを解かない");

        var alert = result.Events.OfType<SoftwareStopExecuted>().Should().ContainSingle().Which;
        alert.Outcome.Should().Be(SoftwareStopOutcome.StopCancelUnconfirmed);
        alert.Quantity.Should().Be(10);
        alert.CloseOrderId.Should().Be("stop-1");
    }

    // ---- T-10-643: 無関係な銘柄・方向に触らない ----

    [Fact]
    public async Task 取り込みと無関係な銘柄や方向の保護レグには触らない_否定形()
    {
        var h = NewHarness();
        var other = AddBrokerStop(h, symbol: "MSFT", orderId: "stop-msft");
        var japan = AddBrokerStop(h, symbol: "AAPL", market: Market.Japan, orderId: "stop-jp");
        var shortSide = AddBrokerStop(h, entrySide: TradeSide.Sell, orderId: "stop-short");
        var target = AddBrokerStop(h, orderId: "stop-target");

        var result = await h.Adopter.ApplyAsync(Adopted());

        h.Broker.CancelledOrderIds.Should().Equal("stop-target");
        result.Reduced.Should().Be(1);
        foreach (var untouched in new[] { other, japan, shortSide })
        {
            h.Stops.Find(untouched.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
            h.Stops.Find(untouched.EntryDecisionId)!.RemainingProtected.Should().Be(10);
        }
    }

    // ---- T-10-644: 再送で二重に取り消さない ----

    [Fact]
    public async Task 同じ取り込みを再送しても二度取り消さない_否定形()
    {
        var h = NewHarness();
        AddBrokerStop(h);
        var adopted = Adopted();

        var first = await h.Adopter.ApplyAsync(adopted);
        var second = await h.Adopter.ApplyAsync(adopted);

        h.Broker.CancelCount.Should().Be(1, "目標は絶対値（取り込み後の数量）であって差分ではない");
        first.Reduced.Should().Be(1);
        second.Reduced.Should().Be(0);
        second.Events.Should().BeEmpty();
    }

    // ---- T-10-645: 部分的な取り込み／保護を消しすぎない ----

    [Fact]
    public async Task 部分的な取り込みは帳簿だけの行から減らし_生きている逆指値は取り消さない()
    {
        var h = NewHarness();
        var brokerStop = AddBrokerStop(h, quantity: 10, orderId: "stop-live");
        var softwareStop = AddSoftwareStop(h, quantity: 4);
        h.Broker.Positions = [new("AAPL", Market.UnitedStates, 11, 1_000m)];

        // 台帳 14 株 → 11 株（3 株がシステム外で売られた）。
        var result = await h.Adopter.ApplyAsync(Adopted(before: 14, after: 11));

        h.Broker.CancelCount.Should().Be(0, "生きた逆指値を取り消して張り直す取引はしない（無保護の窓を作る）");
        h.Stops.Find(brokerStop.EntryDecisionId)!.RemainingProtected.Should().Be(10, "実注文を持つ行は全部か 0 か");
        h.Stops.Find(softwareStop.EntryDecisionId)!.RemainingProtected.Should().Be(1, "帳簿だけの行が先に吸収する");
        result.Reduced.Should().Be(1);
        result.Events.OfType<SoftwareStopExecuted>().Should().ContainSingle()
            .Which.Outcome.Should().Be(SoftwareStopOutcome.ProtectionReduced);
    }

    [Fact]
    public async Task 新しい建玉照会が建玉の存在を示すなら保護を消さない_否定形()
    {
        // 取り込みの観測は最大 60 分古い。その間に利用者が買い戻していれば、消すのは**実在する建玉の保護**である。
        var h = NewHarness();
        var stop = AddBrokerStop(h);
        h.Broker.Positions = [new("AAPL", Market.UnitedStates, 10, 1_000m)];

        var result = await h.Adopter.ApplyAsync(Adopted());

        h.Broker.CancelCount.Should().Be(0);
        h.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
        result.Reduced.Should().Be(0);
        result.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task 建玉を照会できない構成では取り込みの観測に従う()
    {
        // 建玉照会を持たない発注先（内蔵 paper）。何もしないと孤立した逆指値が残る側なので、
        // 利用者が承認した取り込みの観測に従う。
        var h = NewHarness(withPositionSource: false);
        var stop = AddBrokerStop(h);

        var result = await h.Adopter.ApplyAsync(Adopted());

        h.Broker.CancelCount.Should().Be(1);
        h.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        result.Reduced.Should().Be(1);
    }

    // 建玉照会が**例外で落ちた**ときも「不明」と同じ側へ倒す（照会できない構成と区別しない）。
    [Fact]
    public async Task 建玉照会が例外で落ちても取り込みの観測に従う()
    {
        var h = NewHarness();
        h.Broker.PositionsThrow = true;
        var stop = AddBrokerStop(h);

        var result = await h.Adopter.ApplyAsync(Adopted());

        h.Broker.CancelCount.Should().Be(1, "何もしないと孤立した逆指値が残る");
        h.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        result.Reduced.Should().Be(1);
    }

    // 🔴 停止要求は**行の処理を終えてから**見る。1 行目の取消を送った直後に停止されても、
    // その行の結果（取消・保護減少）は返って発行される（無音にしない）。残りの行は Active のまま。
    [Fact]
    public async Task 停止要求で打ち切っても処理済みの行の結果は握り潰さない_否定形()
    {
        var h = NewHarness();
        using var cts = new CancellationTokenSource();
        h.Broker.OnCancelSent = cts.Cancel;
        var first = AddBrokerStop(h, quantity: 10, orderId: "stop-a");
        var second = AddBrokerStop(h, quantity: 10, orderId: "stop-b");

        var result = await h.Adopter.ApplyAsync(Adopted(before: 20, after: 0), cts.Token);

        h.Broker.CancelCount.Should().Be(1, "停止要求の後は次の行へ進まない");
        result.Reduced.Should().Be(1);
        result.Events.OfType<OrderCancelled>().Should().ContainSingle("送った取消の結果を握り潰さない");
        result.Events.OfType<SoftwareStopExecuted>().Should().ContainSingle()
            .Which.Outcome.Should().Be(SoftwareStopOutcome.ProtectionReduced);

        var processed = h.Stops.Find(first.EntryDecisionId)!.State == ProtectiveStopState.Completed ? first : second;
        var untouched = processed == first ? second : first;
        h.Stops.Find(processed.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        h.Stops.Find(untouched.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active,
            "打ち切った残りは Active のまま＝次の観測・巡回・取り込みが引き続き見る");
    }

    [Fact]
    public async Task 増える方向や方向の反転の取り込みでは保護を削らない_否定形()
    {
        var h = NewHarness();
        var stop = AddBrokerStop(h);

        var increased = await h.Adopter.ApplyAsync(Adopted(before: 10, after: 12));
        var reversed = await h.Adopter.ApplyAsync(Adopted(before: 10, after: -3));

        increased.Events.Should().BeEmpty();
        reversed.Events.Should().BeEmpty();
        h.Broker.CancelCount.Should().Be(0);
        h.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }
}
