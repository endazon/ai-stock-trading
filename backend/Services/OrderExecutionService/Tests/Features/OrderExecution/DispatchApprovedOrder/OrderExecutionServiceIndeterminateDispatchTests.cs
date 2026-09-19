using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// 🔴 T-10-408, FR-05, FR-10, FR-11, UC-06, #848, IADR-0117（2026-09-19 追記・改定 6）:
// **発注を送信した後に結果を確認できなかったとき、「拒否された」とも「建玉は生じていない」とも仮定しない。**
//
// 本 PR（#848）は終端になった決済承認を処理中から外した。その結果 `OrderStatus.Rejected` が
// **在庫解放の引き金**になった。発注側の包括 catch が「届いたか不明」を終端 Rejected へ畳んでいたため、
//   - 決済では、証券会社側で生きているかもしれない手仕舞いの押さえが解け、再要求が通って
//     **同じ株数に 2 本の決済が並ぶ（二重決済でショート化）**、
//   - エントリーでは「建玉は生じていない」という仮定になり、注文が実際には生きていた場合に
//     **保護レグを張らないまま無保護の建玉**ができる。
// 本クラスはその両方を否定形で固定する。**予約は解放も確定もせず Reserved のまま据え置き**、
// client order id によるリコンサイル（IADR-0092 / IADR-0074）が実状態を解決することも併せて固定する。
public class OrderExecutionServiceIndeterminateDispatchTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 6, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // 送信は済んだが結果を確認できなかったブローカー（返信待ちのタイムアウト）。
    // IClientOrderIdBroker を実装する＝ DecisionId を remark として伝播する経路＝リコンサイルの前提。
    // IProtectiveOrderBroker も実装する（実装しないと Open が見送られ、本件の経路へ到達しない）。
    private sealed class IndeterminateBroker
        : IBrokerAdapter, IClientOrderIdBroker, IProtectiveOrderBroker
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public int PlaceCount { get; private set; }

        private static BrokerDispatchIndeterminateException Indeterminate() =>
            new("moomoo へ発注を送信しましたが結果を確認できませんでした（テスト）",
                new TimeoutException("返信待ちタイムアウト（テスト）"));

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, Guid decisionId, CancellationToken ct = default)
        {
            PlaceCount++;
            throw Indeterminate();
        }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            PlaceCount++;
            throw Indeterminate();
        }

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            throw Indeterminate();

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default) =>
            throw Indeterminate();

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubProbe(ReservationProbeResult result) : IReservationBrokerProbe
    {
        public Task<ReservationProbeResult> ProbeAsync(
            OrderDispatchReservation reservation, CancellationToken ct = default) =>
            Task.FromResult(result);
    }

    private static OrderIntent Intent(PositionEffect effect) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
            Quantity: 10, Price: 1_000m, effect,
            StopLossPrice: effect == PositionEffect.Open ? 950m : null);

    private static OrderApproved Approved(PositionEffect effect)
    {
        var intent = Intent(effect);
        return new OrderApproved(Guid.NewGuid(), intent, intent.Quantity, Now);
    }

    // 🔴 T-10-408（否定形・最重要）: 決済でもエントリーでも、
    // **結果を確認できない発注は台帳へ終端として記録されない**（＝在庫解放の引き金にならない／
    // 建玉が無いという仮定にならない）。予約は Reserved のまま据え置かれる。
    [Theory]
    [InlineData(PositionEffect.Close)]
    [InlineData(PositionEffect.Open)]
    public async Task 結果を確認できない発注は終端として記録せず予約を据え置く_否定形(PositionEffect effect)
    {
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var broker = new IndeterminateBroker();
        var service = new AppSvc(broker, store, reservations, new FakeClock());
        var approved = Approved(effect);

        var act = async () => await service.ExecuteAsync(approved);

        await act.Should().ThrowAsync<BrokerDispatchIndeterminateException>(
            "見送り（発注していないという主張）にも、拒否（受理されなかったという主張）にも畳まない");
        broker.PlaceCount.Should().Be(1, "送信自体は 1 回行われている");
        store.GetAll().Should().BeEmpty(
            "実在しない注文 ID で終端の記録を作らない——Rejected は在庫解放の引き金であり、"
            + "エントリーでは『建玉は生じていない』という仮定になる");

        var reservation = reservations.Find(approved.DecisionId);
        reservation.Should().NotBeNull("確実に未発注ではない——解放すると再配送で二重発注になる");
        reservation!.State.Should().Be(OrderDispatchState.Reserved, "確定もしない（結果を知らないため）");
        reservation.BrokerOrderId.Should().BeNull("注文 ID を捏造しない");
    }

    // 🔴 T-10-408（否定形・最重要・二重決済）: 手仕舞いの結果が不明なまま在庫の押さえが解けない。
    // 台帳（リスク管理）へ渡る事実は `OrderExecuted` だけであり、本経路はそれを 1 通も作らない。
    [Fact]
    public async Task 結果を確認できない手仕舞いは在庫解放の引き金を作らない_否定形()
    {
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var service = new AppSvc(new IndeterminateBroker(), store, reservations, new FakeClock());
        var approved = Approved(PositionEffect.Close);

        var act = async () => await service.ExecuteAsync(approved);
        await act.Should().ThrowAsync<BrokerDispatchIndeterminateException>();

        store.FindByDecisionId(approved.DecisionId).Should().BeNull(
            "終端 Rejected を記録すると OrderExecuted(Rejected) が台帳へ届き、処理中の決済から外れる＝"
            + "証券会社側で生きているかもしれない手仕舞いの押さえが解け、同じ株数に 2 本目の決済が並ぶ");
    }

    // 🔴 T-10-408: 据え置いた予約は**リコンサイルで後から解決できる**（勝手に完了させない）。
    // 実際に発注されていたなら、**ブローカーが持つ本物の注文 ID**で記録・確定される。
    [Fact]
    public async Task 据え置いた予約はリコンサイルが本物の注文IDで解決する()
    {
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var broker = new IndeterminateBroker();
        var approved = Approved(PositionEffect.Close);

        var dispatch = async () => await new AppSvc(broker, store, reservations, new FakeClock())
            .ExecuteAsync(approved);
        await dispatch.Should().ThrowAsync<BrokerDispatchIndeterminateException>();

        // 滞留 Reserved として拾える（据え置きの意味はここにある）。
        reservations.FindStalledReserved(Now.AddMinutes(1), batchSize: 10)
            .Should().ContainSingle().Which.DecisionId.Should().Be(approved.DecisionId);

        // 実照会で「発注されていた」と確定した場合。
        var real = new BrokerOrder("1149564921959476304", approved.Intent, OrderStatus.Accepted,
            FilledQuantity: 0, AveragePrice: 0m, PlacedAt: Now, CompletedAt: null);
        var result = await new OrderReservationReconciler(
                reservations, store, new StubProbe(ReservationProbeResult.Placed(real)), broker, new FakeClock())
            .ReconcileAsync(Now.AddMinutes(1), batchSize: 10);

        result.Executed.Should().ContainSingle().Which.OrderId.Should().Be("1149564921959476304",
            "自前採番の偽 ID ではなく、証券会社が採番した実在の注文 ID で記録される");
        store.FindByDecisionId(approved.DecisionId)!.Status.Should().Be(OrderStatus.Accepted,
            "生きている注文を『拒否』として台帳へ残さない");
        reservations.Find(approved.DecisionId)!.State.Should().Be(OrderDispatchState.Completed);
    }

    // 🔴 T-10-408（否定形）: 照会でも確定できないあいだは**据え置いたまま**である。
    // 「解決できないから解放する」へ倒すと、届いていた注文の予約を解放して二重発注になる。
    [Fact]
    public async Task 照会でも確定できないあいだは据え置いたまま_否定形()
    {
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var broker = new IndeterminateBroker();
        var approved = Approved(PositionEffect.Close);

        var dispatch = async () => await new AppSvc(broker, store, reservations, new FakeClock())
            .ExecuteAsync(approved);
        await dispatch.Should().ThrowAsync<BrokerDispatchIndeterminateException>();

        var result = await new OrderReservationReconciler(
                reservations, store, new StubProbe(ReservationProbeResult.Indeterminate), broker, new FakeClock())
            .ReconcileAsync(Now.AddMinutes(1), batchSize: 10);

        result.Indeterminate.Should().Be(1);
        reservations.Find(approved.DecisionId)!.State.Should().Be(OrderDispatchState.Reserved);
        store.GetAll().Should().BeEmpty();
    }

    // 🔴 T-10-408（是正で**変えてはいけない**側）: 接続確立の失敗（**確実に未発注**）は従来どおり
    // 見送りで正常終了し、予約を解放する。不明との扱いの違いがこの 1 対である。
    [Fact]
    public async Task 確実に未発注の接続失敗は従来どおり見送りで予約を解放する()
    {
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var approved = Approved(PositionEffect.Close);

        var result = await new AppSvc(new UnavailableBroker(), store, reservations, new FakeClock())
            .ExecuteAsync(approved);

        result.Forgone.Should().NotBeNull();
        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.BrokerUnavailable);
        reservations.Find(approved.DecisionId).Should().BeNull("確実に未発注のため解放してよい");
    }

    // ---- 🔴 T-10-408: 実アダプタを通した結線（#848 で実際に壊れていた経路そのもの） ----
    //
    // 上の 5 件はアプリケーション層の契約を固定する（ブローカーは fake）。ここでは **実際の
    // MoomooBrokerAdapter** に返信待ちのタイムアウトを食わせ、**発注執行の出口まで**同じ結論になることを固定する。
    // 是正前はここで `OrderExecuted(Rejected, 約定 0, 偽 OrderId)` が作られ、
    // それがリスク管理の在庫解放の引き金になっていた。

    // 返信を返さない OpenD クライアント（SendAsync の後で待ちが切れた状況）。
    private sealed class TimingOutTradeClient : Infrastructure.ExternalServices.IMoomooTradeClient
    {
        public bool Sent { get; private set; }

        public Task<Infrastructure.ExternalServices.MoomooOrderResult> PlaceOrderAsync(
            Infrastructure.ExternalServices.MoomooOrderRequest request, CancellationToken ct = default)
        {
            Sent = true; // 🔴 送信は済んでいる。だから「届いたか不明」であって「未発注」ではない。
            throw new TimeoutException("OpenD の返信待ちがタイムアウトしました（テスト）");
        }

        public Task<Infrastructure.ExternalServices.MoomooOrderResult?> QueryOrderAsync(
            string orderId, CancellationToken ct = default) =>
            Task.FromResult<Infrastructure.ExternalServices.MoomooOrderResult?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<Infrastructure.ExternalServices.MoomooOrderSnapshot?> FindOrderByClientIdAsync(
            string clientOrderId, DateTimeOffset reservedAtUtc, CancellationToken ct = default) =>
            Task.FromResult<Infrastructure.ExternalServices.MoomooOrderSnapshot?>(null);

        public Task<IReadOnlyList<Infrastructure.ExternalServices.MoomooPositionSnapshot>> GetPositionsAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Infrastructure.ExternalServices.MoomooPositionSnapshot>>([]);

        public Task<Infrastructure.ExternalServices.MoomooAccountType?> GetAccountTypeAsync(
            CancellationToken ct = default) =>
            Task.FromResult<Infrastructure.ExternalServices.MoomooAccountType?>(
                Infrastructure.ExternalServices.MoomooAccountType.Margin);
    }

    // 🔴 T-10-408（否定形・最重要）: **手仕舞いの送信後タイムアウトで在庫が解放されない。**
    // 是正前は Rejected（終端）が台帳へ届き、処理中の決済から外れて押さえが解けた
    //（＝同じ株数に 2 本目の決済が並ぶ＝二重決済でショート化）。
    [Fact]
    public async Task 実アダプタ経由_手仕舞いの送信後タイムアウトで在庫解放の引き金を作らない_否定形()
    {
        var client = new TimingOutTradeClient();
        var broker = new Infrastructure.ExternalServices.MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate);
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var approved = Approved(PositionEffect.Close);

        var act = async () => await new AppSvc(broker, store, reservations, new FakeClock()).ExecuteAsync(approved);

        await act.Should().ThrowAsync<BrokerDispatchIndeterminateException>();
        client.Sent.Should().BeTrue("送信済みであることが『不明』の根拠である");
        store.GetAll().Should().BeEmpty(
            "Rejected の終端記録＝在庫解放の引き金。状態が不明なまま押さえを解くと二重決済でショート化する");
        reservations.Find(approved.DecisionId)!.State.Should().Be(OrderDispatchState.Reserved);
    }

    // 🔴 T-10-408（否定形・最重要）: **エントリーで「建玉は生じていない」と仮定しない。**
    // 是正前は Rejected（終端失敗）となり、保護レグを張らずに正常終了していた
    //（注文が実際には生きていた場合、**無保護の建玉**がそのまま残る）。
    [Fact]
    public async Task 実アダプタ経由_エントリーの送信後タイムアウトで建玉なしと仮定しない_否定形()
    {
        var client = new TimingOutTradeClient();
        var broker = new Infrastructure.ExternalServices.MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate);
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var approved = Approved(PositionEffect.Open);

        var act = async () => await new AppSvc(broker, store, reservations, new FakeClock()).ExecuteAsync(approved);

        await act.Should().ThrowAsync<BrokerDispatchIndeterminateException>(
            "『終端失敗だから建玉は生じない＝保護レグは不要』へ倒すと、生きていた注文が無保護の建玉になる");
        store.GetAll().Should().BeEmpty("偽の注文 ID を持つ終端記録を 7 年保持の台帳へ残さない");
        reservations.Find(approved.DecisionId)!.State.Should().Be(OrderDispatchState.Reserved,
            "実状態の解決はリコンサイルに委ねる（予約を勝手に完了させない）");
    }

    private sealed class UnavailableBroker : IBrokerAdapter, IClientOrderIdBroker, IProtectiveOrderBroker
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, Guid decisionId, CancellationToken ct = default) =>
            throw new BrokerUnavailableException("OpenD へ接続できません（テスト・未発注）");

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new BrokerUnavailableException("OpenD へ接続できません（テスト・未発注）");

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            throw new BrokerUnavailableException("OpenD へ接続できません（テスト・未発注）");

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default) =>
            throw new BrokerUnavailableException("OpenD へ接続できません（テスト・未発注）");

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
