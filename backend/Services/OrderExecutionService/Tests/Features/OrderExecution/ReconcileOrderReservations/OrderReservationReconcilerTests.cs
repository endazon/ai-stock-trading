using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace OrderExecutionService.Tests;

// #141, FR-05, IADR-0074: 滞留 Reserved の自動リコンサイル。受け入れ基準1/2（発注済み→確定・未発注→解放・
// 不確定は解放しない）を fake プローブで検証する。二重発注を絶対に起こさない fail-safe が最優先。
public class OrderReservationReconcilerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StalledAt = Now.AddHours(-48);
    private static readonly DateTimeOffset Cutoff = Now.AddHours(-24);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // 指定した結果を返し、照会回数を数えるフェイク・プローブ。
    private sealed class FakeProbe(ReservationProbeResult result) : IReservationBrokerProbe
    {
        public int Calls { get; private set; }

        public Task<ReservationProbeResult> ProbeAsync(
            OrderDispatchReservation reservation, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    // 照会ごとに任意の副作用＋結果を差し込めるフェイク（TOCTOU・例外の再現用）。
    private sealed class CallbackProbe(Func<Guid, ReservationProbeResult> fn) : IReservationBrokerProbe
    {
        public Task<ReservationProbeResult> ProbeAsync(
            OrderDispatchReservation reservation, CancellationToken cancellationToken = default) =>
            Task.FromResult(fn(reservation.DecisionId));
    }

    private static OrderIntent Intent(string symbol = "AAPL", int qty = 10, decimal price = 100m) =>
        new(symbol, Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, qty, price);

    private static BrokerOrder Placed(string orderId, OrderStatus status = OrderStatus.Filled) =>
        new(orderId, Intent(), status, FilledQuantity: 10, AveragePrice: 101m,
            PlacedAt: StalledAt, CompletedAt: StalledAt);

    // 🔴 T-10-600, #856, IADR-0362: 解放の門（Reconciliation:ReleaseOnNotPlaced）は**既定で閉じている**。
    // 明示的に開けたときだけ NotPlaced が解放へ進む。テストも既定は閉じた側で組む。
    private static IOptions<ReconciliationOptions> Options(bool releaseOnNotPlaced) =>
        Microsoft.Extensions.Options.Options.Create(
            new ReconciliationOptions { Enabled = true, ReleaseOnNotPlaced = releaseOnNotPlaced });

    private static (OrderReservationReconciler Reconciler, InMemoryOrderReservationStore Reservations,
        InMemoryExecutedOrderStore Executed, FakeProbe Probe) Build(
            ReservationProbeResult probeResult, bool releaseOnNotPlaced = false)
    {
        var reservations = new InMemoryOrderReservationStore();
        var executed = new InMemoryExecutedOrderStore();
        var probe = new FakeProbe(probeResult);
        var reconciler = new OrderReservationReconciler(
            reservations, executed, probe, new PaperBrokerAdapter(), new FakeClock(), Options(releaseOnNotPlaced));
        return (reconciler, reservations, executed, probe);
    }

    [Fact]
    public async Task 記録ありの滞留予約はブローカ照会せず自己修復で確定する()
    {
        // phase-4 断絶（Save 成功・MarkCompleted 失敗）: 記録はあるが予約は Reserved のまま。ブローカに聞かず確定する。
        var (reconciler, reservations, executed, probe) = Build(ReservationProbeResult.Indeterminate);
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt);
        executed.Save(new ExecutionRecord(
            decisionId, "BRK-1", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            PositionEffect.Open, 10, 100m, 10, 101m, OrderStatus.Filled, 0.01m, StalledAt));

        var result = await reconciler.ReconcileAsync(Cutoff, batchSize: 50);

        probe.Calls.Should().Be(0, "記録があればブローカ照会は不要");
        result.Terminalized.Should().Be(1);
        reservations.Find(decisionId)!.State.Should().Be(OrderDispatchState.Completed);
        reservations.Find(decisionId)!.BrokerOrderId.Should().Be("BRK-1");
        result.Executed.Should().ContainSingle().Which.OrderId.Should().Be("BRK-1");
    }

    [Fact]
    public async Task 発注済みが確定した滞留予約は記録を保存し確定する()
    {
        // 受け入れ基準1（発注済み→確定）: プローブが Placed を返す。記録が保存され、予約は確定し、OrderExecuted が載る。
        var (reconciler, reservations, executed, _) = Build(ReservationProbeResult.Placed(Placed("BRK-9")));
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt);

        var result = await reconciler.ReconcileAsync(Cutoff, batchSize: 50);

        result.Terminalized.Should().Be(1);
        result.Released.Should().Be(0);
        executed.FindByDecisionId(decisionId).Should().NotBeNull();
        executed.FindByDecisionId(decisionId)!.OrderId.Should().Be("BRK-9");
        reservations.Find(decisionId)!.State.Should().Be(OrderDispatchState.Completed);
        result.Executed.Should().ContainSingle().Which.DecisionId.Should().Be(decisionId);
    }

    [Fact]
    public async Task 未発注が確定した滞留予約は解放される()
    {
        // 受け入れ基準1（未発注→解放）: プローブが NotPlaced を返す。予約は削除され、再配送で改めて発注できる。
        // 🔴 T-10-601, #856: **解放の門を明示的に開けたときだけ**この経路へ進む（既定は閉じている＝T-10-600）。
        var (reconciler, reservations, executed, _) =
            Build(ReservationProbeResult.NotPlaced, releaseOnNotPlaced: true);
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt);

        var result = await reconciler.ReconcileAsync(Cutoff, batchSize: 50);

        result.Released.Should().Be(1);
        result.Terminalized.Should().Be(0);
        reservations.Find(decisionId).Should().BeNull("未発注と確定した予約は解放（削除）される");
        executed.FindByDecisionId(decisionId).Should().BeNull("発注していないので記録は作らない");
        result.Executed.Should().BeEmpty();
    }

    [Fact]
    public async Task 照会不確定の滞留予約は据え置かれ解放されない()
    {
        // 受け入れ基準2（fail-safe）: Indeterminate は「発注済みか不明」であり、解放すれば二重発注を招く。据え置く。
        var (reconciler, reservations, _, _) = Build(ReservationProbeResult.Indeterminate);
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt);

        var result = await reconciler.ReconcileAsync(Cutoff, batchSize: 50);

        result.Indeterminate.Should().Be(1);
        result.Released.Should().Be(0);
        result.Terminalized.Should().Be(0);
        reservations.Find(decisionId)!.State.Should().Be(OrderDispatchState.Reserved, "不確定は据え置く");
        result.Executed.Should().BeEmpty();
    }

    [Fact]
    public async Task 既定noopプローブでは何も解放も終端化もされない()
    {
        // fail-safe の要: 既定 no-op プローブ（常に Indeterminate）下では、多数の滞留があっても一切触れない。
        var reservations = new InMemoryOrderReservationStore();
        var executed = new InMemoryExecutedOrderStore();
        var reconciler = new OrderReservationReconciler(
            reservations, executed, new IndeterminateReservationBrokerProbe(), new PaperBrokerAdapter(), new FakeClock(),
            Options(releaseOnNotPlaced: true));
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids)
            reservations.TryReserve(id, StalledAt);

        var result = await reconciler.ReconcileAsync(Cutoff, batchSize: 50);

        result.Scanned.Should().Be(5);
        result.Indeterminate.Should().Be(5);
        result.Released.Should().Be(0);
        result.Terminalized.Should().Be(0);
        ids.Should().OnlyContain(id => reservations.Find(id)!.State == OrderDispatchState.Reserved);
    }

    [Fact]
    public async Task 滞留閾値内の予約はリコンサイル対象にならない()
    {
        // in-flight（cutoff より新しい）予約は滞留と誤認しない（再配送中の予約に触れて二重発注しないため）。
        var (reconciler, reservations, _, probe) = Build(ReservationProbeResult.NotPlaced);
        var recent = Guid.NewGuid();
        reservations.TryReserve(recent, Now.AddMinutes(-1)); // cutoff（-24h）より新しい

        var result = await reconciler.ReconcileAsync(Cutoff, batchSize: 50);

        result.Scanned.Should().Be(0);
        probe.Calls.Should().Be(0);
        reservations.Find(recent)!.State.Should().Be(OrderDispatchState.Reserved);
    }

    [Fact]
    public async Task batchSizeを超える滞留は一巡回で処理しきらない()
    {
        var (reconciler, reservations, _, _) = Build(ReservationProbeResult.Indeterminate);
        for (var i = 0; i < 5; i++)
            reservations.TryReserve(Guid.NewGuid(), StalledAt.AddSeconds(i));

        var result = await reconciler.ReconcileAsync(Cutoff, batchSize: 2);

        result.Scanned.Should().Be(2);
    }

    [Fact]
    public async Task 照会中に通常フローが確定した場合は二重保存せず自己修復する()
    {
        // TOCTOU: 照会（非同期）の最中に通常フロー（OrderApprovedConsumer）が同一 DecisionId を確定した状況を模す。
        // Save 直前の再確認で既存記録を用い、二重保存（実運用の主キー競合）を避け自己修復に倒す。
        var reservations = new InMemoryOrderReservationStore();
        var executed = new InMemoryExecutedOrderStore();
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt);
        var probe = new CallbackProbe(id =>
        {
            executed.Save(new ExecutionRecord(
                id, "BRK-RACE", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
                PositionEffect.Open, 10, 100m, 10, 100m, OrderStatus.Filled, 0m, StalledAt));
            return ReservationProbeResult.Placed(Placed("BRK-PROBE"));
        });
        var reconciler = new OrderReservationReconciler(
            reservations, executed, probe, new PaperBrokerAdapter(), new FakeClock(), Options(releaseOnNotPlaced: true));

        var result = await reconciler.ReconcileAsync(Cutoff, batchSize: 50);

        result.Terminalized.Should().Be(1);
        result.Failed.Should().Be(0);
        executed.GetAll().Should().ContainSingle("照会中に確定済みなら二重保存しない");
        reservations.Find(decisionId)!.State.Should().Be(OrderDispatchState.Completed);
        reservations.Find(decisionId)!.BrokerOrderId.Should().Be("BRK-RACE", "既存記録の注文 ID で確定する");
    }

    [Fact]
    public async Task 一件の例外はバッチ全体を止めず据え置かれ他は処理される()
    {
        // 1 件の照会例外で残り全件を巻き添えにしない（次回巡回で再試行＝据え置き）。安全側は保たれる。
        var reservations = new InMemoryOrderReservationStore();
        var executed = new InMemoryExecutedOrderStore();
        var bad = Guid.NewGuid();
        var good = Guid.NewGuid();
        reservations.TryReserve(bad, StalledAt); // ReservedAt 昇順で先頭
        reservations.TryReserve(good, StalledAt.AddSeconds(1));
        var probe = new CallbackProbe(id =>
            id == bad ? throw new InvalidOperationException("照会失敗") : ReservationProbeResult.NotPlaced);
        // #856: 「1 件の失敗が他を巻き添えにしない」を見るテストなので、解放の門は開けた側で組む。
        var reconciler = new OrderReservationReconciler(
            reservations, executed, probe, new PaperBrokerAdapter(), new FakeClock(), Options(releaseOnNotPlaced: true));

        var result = await reconciler.ReconcileAsync(Cutoff, batchSize: 50);

        result.Scanned.Should().Be(2);
        result.Failed.Should().Be(1);
        result.Released.Should().Be(1);
        reservations.Find(bad)!.State.Should().Be(OrderDispatchState.Reserved, "失敗は据え置き");
        reservations.Find(good).Should().BeNull("他の予約は処理される");
    }

    // ---- 🔴 #856, IADR-0362: 解放の門（ReleaseOnNotPlaced）と「黙って通り過ぎない」 ----

    [Fact]
    public async Task 解放の門が閉じているあいだは未発注と出ても解放しない()
    {
        // 🔴 T-10-600（否定形・最重要）: `NotPlaced` は「確実に未発注」を名乗るが、その根拠は
        // **moomoo SIMULATE で remark（client order id）が往復する**という実機未検証の前提である。
        // 往復しなければ発注済みの注文が 1 件も一致せず**全件が NotPlaced＝全件解放＝二重発注**になる。
        // 門が閉じているあいだは、プローブが何と言おうと在庫の押さえを解かない。
        var (reconciler, reservations, executed, _) = Build(ReservationProbeResult.NotPlaced);
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt);

        var result = await reconciler.ReconcileAsync(Cutoff, batchSize: 50);

        result.Released.Should().Be(0, "門が閉じているあいだは 1 件も解放しない");
        result.Terminalized.Should().Be(0);
        reservations.Find(decisionId).Should().NotBeNull("予約は残る");
        reservations.Find(decisionId)!.State.Should().Be(OrderDispatchState.Reserved, "据え置く");
        executed.FindByDecisionId(decisionId).Should().BeNull("発注していないと決めつけて記録も作らない");
        result.Executed.Should().BeEmpty();
    }

    [Fact]
    public async Task 門が閉じた未発注判定は件数に残り無音にならない()
    {
        // T-10-603: 据え置きを無音にしない。門が閉じて据え置いた `NotPlaced` は結果に載り、
        // 常駐が警告でログする（実機検証が済めば門を開ける、という運用判断の入力になる）。
        var (reconciler, reservations, _, _) = Build(ReservationProbeResult.NotPlaced);
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt);

        var result = await reconciler.ReconcileAsync(Cutoff, batchSize: 50);

        result.HeldNotPlaced.Should().ContainSingle().Which.Should().Be(decisionId);
    }

    [Fact]
    public async Task 門を開けたときだけ未発注判定が解放へ進む()
    {
        // T-10-601: 門の実効。開ければ従来どおり解放する（#141 / IADR-0074 の受け入れ基準1）。
        var (reconciler, reservations, _, _) = Build(ReservationProbeResult.NotPlaced, releaseOnNotPlaced: true);
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt);

        var result = await reconciler.ReconcileAsync(Cutoff, batchSize: 50);

        result.Released.Should().Be(1);
        result.HeldNotPlaced.Should().BeEmpty("解放したものは据え置きではない");
        reservations.Find(decisionId).Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 不確定は門の開閉に依らず解放されない(bool releaseOnNotPlaced)
    {
        // 🔴 T-10-602（否定形）: 門は `NotPlaced` にしか効かない。`Indeterminate`（照会不達・判定不能）は
        // 門を開けても据え置く——門を開けることが「不明も解放してよい」へ広がらないことを固定する。
        var (reconciler, reservations, _, _) = Build(ReservationProbeResult.Indeterminate, releaseOnNotPlaced);
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt);

        var result = await reconciler.ReconcileAsync(Cutoff, batchSize: 50);

        result.Indeterminate.Should().Be(1);
        result.Released.Should().Be(0);
        result.HeldNotPlaced.Should().BeEmpty("不確定は「未発注と出たが据え置いた」ではない");
        reservations.Find(decisionId)!.State.Should().Be(OrderDispatchState.Reserved);
    }

    [Fact]
    public async Task 突合で発注済みと確定した終端化は結果に載る()
    {
        // 🔴 T-10-604: 突合が `Placed` で終端化したエントリーには**保護逆指値が張られない**（#853 の 2 番）。
        // 有効化するとこの経路が実際に踏まれるので、**黙って通り過ぎない**——結果に載せ、常駐が Critical でログする。
        var (reconciler, reservations, _, _) = Build(ReservationProbeResult.Placed(Placed("BRK-P1")));
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt);

        var result = await reconciler.ReconcileAsync(Cutoff, batchSize: 50);

        result.Terminalized.Should().Be(1);
        var finding = result.ProbeTerminalized.Should().ContainSingle().Subject;
        finding.DecisionId.Should().Be(decisionId);
        finding.OrderId.Should().Be("BRK-P1");
        finding.Symbol.Should().Be("AAPL");
        finding.Quantity.Should().Be(10);
    }

    [Fact]
    public async Task 自己修復による終端化は突合の結果には載らない()
    {
        // T-10-605: phase-4 自己修復（記録あり）はブローカへ照会していない＝突合ではない。
        // 記録は通常フローが作ったものであり、保護レグの有無も通常フローが決めている。混ぜない。
        var (reconciler, reservations, executed, _) = Build(ReservationProbeResult.Indeterminate);
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt);
        executed.Save(new ExecutionRecord(
            decisionId, "BRK-SELF", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            PositionEffect.Open, 10, 100m, 10, 101m, OrderStatus.Filled, 0.01m, StalledAt));

        var result = await reconciler.ReconcileAsync(Cutoff, batchSize: 50);

        result.Terminalized.Should().Be(1);
        result.ProbeTerminalized.Should().BeEmpty("自己修復はブローカ照会による確定ではない");
    }

    [Fact]
    public void 構成の既定は三つとも閉じている()
    {
        // T-10-607: fail-safe 既定の固定。有効化は**配備（Helm values）で明示的に**行う規律であり、
        // アプリ既定が勝手に開くと docker-compose・単体開発環境まで挙動が変わる。
        var options = new ReconciliationOptions();

        options.Enabled.Should().BeFalse();
        options.UseBrokerProbe.Should().BeFalse();
        options.ReleaseOnNotPlaced.Should().BeFalse();
    }

    [Fact]
    public async Task 完了済み予約は走査対象に含まれない()
    {
        // 終端（Completed）予約はリコンサイル対象外（FindStalledReserved が Reserved のみ返す）。
        var (reconciler, reservations, _, probe) = Build(ReservationProbeResult.NotPlaced);
        var done = Guid.NewGuid();
        reservations.TryReserve(done, StalledAt);
        reservations.MarkCompleted(done, "BRK-DONE", StalledAt);

        var result = await reconciler.ReconcileAsync(Cutoff, batchSize: 50);

        result.Scanned.Should().Be(0);
        probe.Calls.Should().Be(0);
        reservations.Find(done)!.State.Should().Be(OrderDispatchState.Completed);
    }
}
