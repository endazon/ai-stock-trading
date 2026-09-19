using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using AwesomeAssertions.Execution;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// 🔴 T-10-450, FR-05, FR-10, FR-11, UC-06, #848, IADR-0117（2026-09-19 追記・改定 8）:
// **発注応答の retType のうち「返事を読めなかった」系の値を、「確認できた拒否」に畳まない。**
//
// PR #851 の 4 巡目監査（B5）の実測: MMApiMoomooTradeClient.EnsureSucceeded は retType != 0 をすべて
// MoomooTradeRequestException にし、MoomooBrokerAdapter がそれを丸ごと Terminal(Rejected) へ倒していた。
//
//   retType=-100/-400/-200/-1: OrderExecuted.Status=Rejected orderId=<偽 32hex> reservation=Completed
//
// SDK の列挙は Succeed=0 / Failed=-1 / TimeOut=-100 / DisConnect=-200 / Unknown=-400 / Invalid=-500 であり、
// moomoo-api 10.8.6808 を逆コンパイルして読むと **-100 と -500 は SDK がクライアント側で合成する**
//（-100＝**送信済み**要求の 12 秒打ち切り／-500＝**届いた応答**の復号・パース失敗）。どちらも注文が受理されたかは分からない。
// 🔴 本実装の返信待ちの既定は 15 秒なので、**既定構成の返信待ちタイムアウトは例外ではなく retType=-100 で現れる**
//（改定 6〔T-10-408〕が塞いだ TimeoutException より先に来る）。
//
// Rejected はリスク管理の取引台帳で**在庫解放の引き金**である。不明を Rejected に畳むと、
//   - 決済では、届いていたかもしれない手仕舞いの押さえが解け、再要求が通って**二重決済でショート化**する。
//   - エントリーでは「終端失敗＝建玉は生じない」と読まれ、届いていれば**保護レグ無しの建玉**が残る。
// 本クラスは「例外の型」だけでなく、その**結果**（終端の記録が作られない／保護レグの分岐へ進まない／
// 確認できた拒否 -1 では従来どおり作られる）を、実アダプタを通して固定する。
public class MoomooPlaceOrderRetTypeClassificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 9, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // OpenD / SDK が「非成功の応答」を返すクライアント。実装（MMApiMoomooTradeClient.EnsureSucceeded）と同じく
    // retType != 0 を MoomooTradeRequestException にして投げる。**送信は済んでいる**（応答が返っている）。
    private sealed class NonSuccessReplyTradeClient(int retType, string retMsg) : IMoomooTradeClient
    {
        /// <summary>種別ごとの送信回数（何を何回 OpenD へ送ったか）。</summary>
        public List<MoomooOrderKind> Sent { get; } = [];

        public int CancelCount { get; private set; }

        /// <summary>この種別だけ非成功にする（null＝全種別）。他の種別は受理される。</summary>
        public MoomooOrderKind? OnlyKind { get; init; }

        /// <summary>OnlyKind 以外の種別に返す retType（0＝受理）。</summary>
        public int OtherRetType { get; init; }

        public Task<MoomooOrderResult> PlaceOrderAsync(MoomooOrderRequest request, CancellationToken ct = default)
        {
            Sent.Add(request.Kind);
            if (OnlyKind is null || request.Kind == OnlyKind)
                throw new MoomooTradeRequestException("PlaceOrder", retType, retMsg);
            if (OtherRetType != 0)
                throw new MoomooTradeRequestException("PlaceOrder", OtherRetType, "逆指値は受け付けられません（テスト）");
            return Task.FromResult(new MoomooOrderResult($"mo-{Sent.Count}", MoomooOrderState.Submitted, 0, 0m));
        }

        // 元の逆指値は失効している（ガードのテスト用）。
        public Task<MoomooOrderResult?> QueryOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<MoomooOrderResult?>(new MoomooOrderResult(orderId, MoomooOrderState.Cancelled, 0, 0m));

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            CancelCount++;
            return Task.CompletedTask;
        }

        public Task<MoomooOrderSnapshot?> FindOrderByClientIdAsync(
            string clientOrderId, DateTimeOffset reservedAtUtc, CancellationToken ct = default) =>
            Task.FromResult<MoomooOrderSnapshot?>(null);

        public Task<IReadOnlyList<MoomooPositionSnapshot>> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MoomooPositionSnapshot>>(
                [new MoomooPositionSnapshot("AAPL", MoomooMarket.UnitedStates, 10, 1_000m)]);

        public Task<MoomooAccountType?> GetAccountTypeAsync(CancellationToken ct = default) =>
            Task.FromResult<MoomooAccountType?>(MoomooAccountType.Margin);

        public Task<decimal?> GetAccountEquityInBaseAsync(CancellationToken ct = default) =>
            Task.FromResult<decimal?>(3_000m);
    }

    private static OrderIntent Intent(PositionEffect effect) =>
        new("AAPL", Market.UnitedStates, effect == PositionEffect.Close ? TradeSide.Sell : TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, Quantity: 10, Price: 1_000m, effect,
            StopLossPrice: effect == PositionEffect.Open ? 950m : null);

    private static OrderApproved Approved(PositionEffect effect)
    {
        var intent = Intent(effect);
        return new OrderApproved(Guid.NewGuid(), intent, intent.Quantity, Now);
    }

    // ---- 値域の固定 ----

    // 🔴 分類の根拠は SDK の列挙の値である。SDK の更新で値が動いた・**値が増えた**ら、ここで落として分類を見直させる。
    [Fact]
    public void MoomooRetType_の値は_SDK_の列挙と一致する()
    {
        var sdk = Enum.GetValues<Moomoo.OpenApi.Pb.Common.RetType>()
            .ToDictionary(v => v.ToString().Replace("RetType_", string.Empty, StringComparison.Ordinal), v => (int)v);

        sdk.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["Succeed"] = MoomooRetType.Succeed,
            ["Failed"] = MoomooRetType.Failed,
            ["TimeOut"] = MoomooRetType.TimeOut,
            ["DisConnect"] = MoomooRetType.DisConnect,
            ["Unknown"] = MoomooRetType.Unknown,
            ["Invalid"] = MoomooRetType.Invalid,
        });
        (MoomooRetType.Failed, MoomooRetType.TimeOut, MoomooRetType.DisConnect, MoomooRetType.Unknown, MoomooRetType.Invalid)
            .Should().Be((-1, -100, -200, -400, -500));
    }

    [Theory]
    [InlineData(-1, true)]      // Failed: OpenD が返事として失敗を返した＝確認できた非受理
    [InlineData(-100, false)]   // TimeOut: SDK が送信済み要求を 12 秒で打ち切った
    [InlineData(-200, false)]   // DisConnect
    [InlineData(-400, false)]   // Unknown（結果未知）
    [InlineData(-500, false)]   // Invalid: 届いた応答を読めなかった（「要求が不正で未発注」ではない）
    [InlineData(1, false)]      // 未定義の値（知らないコードは確認できた失敗としない）
    [InlineData(-2, false)]
    [InlineData(-999, false)]
    public void 確認できた失敗は_retType_がマイナス1_のときだけ(int retType, bool confirmed)
    {
        MoomooRetType.IsConfirmedFailure(retType).Should().Be(confirmed);
        new MoomooTradeRequestException("PlaceOrder", retType, "x").IsConfirmedFailure.Should().Be(confirmed);
    }

    // ---- 写像（アダプタ） ----

    // 🔴 T-10-450（否定形）: 全レグ（通常発注・逆指値・成行・代替注文種別）で、不明系の retType を Rejected へ畳まない。
    [Theory]
    [InlineData(-100)]
    [InlineData(-200)]
    [InlineData(-400)]
    [InlineData(-500)]
    [InlineData(1)]
    [InlineData(-999)]
    public async Task 返事を読めなかった系の_retType_は_全レグで_Rejected_へ畳まず伝播する_否定形(int retType)
    {
        var client = new NonSuccessReplyTradeClient(retType, "（テスト）");
        var adapter = new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate);
        var close = Intent(PositionEffect.Close);

        var entry = async () => await adapter.PlaceOrderAsync(Intent(PositionEffect.Open), Guid.NewGuid());
        var stop = async () => await adapter.PlaceStopOrderAsync(close, 950m, Guid.NewGuid());
        var market = async () => await adapter.PlaceMarketOrderAsync(close, Guid.NewGuid());
        var alternative = async () => await adapter.PlaceAlternativeStopOrderAsync(close, 950m, 1_000m, Guid.NewGuid());

        var thrown = await entry.Should().ThrowAsync<BrokerDispatchIndeterminateException>(
            "retType={0} は『送ったが返事を読めなかった』であり、証券会社が断ったとは確認できていない", retType);
        thrown.Which.Message.Should().Contain($"retType={retType}", "原因（retType / retMsg）が消えると切り分けができない");
        thrown.Which.InnerException.Should().BeOfType<MoomooTradeRequestException>();
        await stop.Should().ThrowAsync<BrokerDispatchIndeterminateException>();
        await market.Should().ThrowAsync<BrokerDispatchIndeterminateException>();
        await alternative.Should().ThrowAsync<BrokerDispatchIndeterminateException>();
        client.Sent.Should().HaveCount(4, "4 本とも送信は済んでいる——だからこそ『不明』である");
    }

    // ---- 🔴 実アダプタを通した結線（監査が実測した経路そのもの）: 結果を固定する ----

    // 🔴 T-10-450（否定形・最重要・二重決済）: **手仕舞いの応答が不明系の retType でも、在庫解放の引き金を作らない。**
    // 是正前はここで OrderExecuted(Rejected, 約定 0, 偽 OrderId) が作られ予約が Completed になり、
    // それがリスク管理の MarkTerminal へ届いて押さえが解けていた。
    [Theory]
    [InlineData(-100)]
    [InlineData(-200)]
    [InlineData(-400)]
    [InlineData(-500)]
    [InlineData(-999)]
    public async Task 実アダプタ経由_手仕舞いの応答が不明系の_retType_なら_在庫解放の引き金を作らない_否定形(int retType)
    {
        var client = new NonSuccessReplyTradeClient(retType, string.Empty);
        var broker = new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate);
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var approved = Approved(PositionEffect.Close);

        var (executed, thrown) = await DispatchAsync(broker, store, reservations, approved);

        // 🔴 例外の型より先に**結果**を固定する（在庫が解放されない）。
        using var scope = new AssertionScope();
        executed.Should().BeNull(
            "OrderExecuted(Rejected) は取引台帳の在庫解放の引き金である。届いたか不明のまま押さえを解くと、"
            + "利用者の再要求が通って同じ株数に 2 本目の決済が並ぶ（二重決済でショート化）");
        store.GetAll().Should().BeEmpty("偽の注文 ID を持つ終端 Rejected の記録を作らない");
        var reservation = reservations.Find(approved.DecisionId);
        reservation.Should().NotBeNull("確実に未発注ではない——解放すると再配送で二重発注になる");
        reservation?.State.Should().Be(OrderDispatchState.Reserved, "是正前は Completed（＝終端として確定）になっていた");
        reservation?.BrokerOrderId.Should().BeNull("注文 ID を捏造しない");
        client.Sent.Should().Equal(MoomooOrderKind.Limit);
        thrown.Should().BeOfType<BrokerDispatchIndeterminateException>("拒否にも見送りにも畳まず、不明のまま伝播する");
    }

    // 発注執行を 1 回まわし、「作られた OrderExecuted」と「伝播した例外」の両方を返す（結果を先に assert するため）。
    private static async Task<(OrderExecuted? Executed, Exception? Thrown)> DispatchAsync(
        IBrokerAdapter broker, InMemoryExecutedOrderStore store, InMemoryOrderReservationStore reservations,
        OrderApproved approved)
    {
        try
        {
            var result = await new AppSvc(broker, store, reservations, new FakeClock()).ExecuteAsync(approved);
            return (result.Executed, null);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    // 🔴 T-10-450（否定形・最重要・無保護の建玉）: **エントリーの応答が不明系の retType でも「建玉は生じていない」と仮定しない。**
    // 是正前は Rejected（終端失敗）として**正常終了**し、保護レグを張らずに終わっていた。
    [Theory]
    [InlineData(-100)]
    [InlineData(-500)]
    public async Task 実アダプタ経由_エントリーの応答が不明系の_retType_なら_建玉なしと仮定しない_否定形(int retType)
    {
        var client = new NonSuccessReplyTradeClient(retType, string.Empty);
        var broker = new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate);
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var approved = Approved(PositionEffect.Open);

        var (executed, thrown) = await DispatchAsync(broker, store, reservations, approved);

        using var scope = new AssertionScope();
        executed.Should().BeNull(
            "『終端失敗（Rejected）だから建玉は生じない＝保護レグは不要』と読んで正常終了すると、"
            + "届いていた注文が無保護の建玉になる");
        store.GetAll().Should().BeEmpty("エントリーを『拒否された』と記録しない");
        reservations.Find(approved.DecisionId)?.State.Should().Be(OrderDispatchState.Reserved,
            "予約を据え置く＝再配送で 2 本目のエントリーを撃たない（是正前は Completed）");
        thrown.Should().BeOfType<BrokerDispatchIndeterminateException>();
    }

    // 🔴 T-10-450（是正で**変えてはいけない**側）: **確認できた拒否（retType=-1）は従来どおり終端 Rejected**。
    // 稼働環境で実測した 2 件の理由文で固定する（#844 の価格精度／#809 の Stop 非対応）。
    // ここを不明へ倒すと、断られた手仕舞いが 30 分の窓の満了まで建玉をロックする（#848 の症状の再発）。
    [Theory]
    [InlineData("The precision of Price in Place Order does not meet the specification.")]
    [InlineData("Paper trading does not support Stop order.")]
    public async Task 実アダプタ経由_確認できた拒否_retType_マイナス1_は従来どおり在庫解放の引き金を作る(string retMsg)
    {
        var client = new NonSuccessReplyTradeClient(MoomooRetType.Failed, retMsg);
        var broker = new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate);
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var approved = Approved(PositionEffect.Close);

        var result = await new AppSvc(broker, store, reservations, new FakeClock()).ExecuteAsync(approved);

        result.Executed.Should().NotBeNull("確認できた拒否は OrderExecuted として台帳へ届ける");
        result.Executed!.Status.Should().Be(OrderStatus.Rejected,
            "Rejected が取引台帳の MarkTerminal へ届き、処理中の決済から外れる（在庫が解放される。台帳側は T-10-401）");
        result.Executed.FilledQuantity.Should().Be(0);
        store.FindByDecisionId(approved.DecisionId)!.Status.Should().Be(OrderStatus.Rejected);
        reservations.Find(approved.DecisionId)!.State.Should().Be(OrderDispatchState.Completed,
            "結果が確定しているので予約も確定する（滞留させない）");
    }

    // 🔴 T-10-450: 確認できた拒否は理由（retType / retMsg）を S3 の戻り値へ載せ続ける（#821 の目的を壊さない）。
    [Fact]
    public async Task 確認できた拒否は代替注文種別でも理由を戻り値へ載せて終端_Rejected()
    {
        var client = new NonSuccessReplyTradeClient(MoomooRetType.Failed, "Paper trading does not support StopLimit order");
        var adapter = new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate);

        var placement = await adapter.PlaceAlternativeStopOrderAsync(
            Intent(PositionEffect.Close), 950m, 1_000m, Guid.NewGuid());

        placement.Order.Status.Should().Be(OrderStatus.Rejected);
        placement.RejectReasonCode.Should().Be(-1);
        placement.RejectReasonMessage.Should().Be("Paper trading does not support StopLimit order");
    }

    // 🔴 T-10-450（否定形・B4 と B5 の結線）: 常駐ガードの成行手仕舞いの応答が retType=-100 でも、巡回ごとに撃ち直さない。
    // 逆指値の再発注は確認できた拒否（-1）で断られ、成行手仕舞いへ落ちる状況。
    // 是正前: 成行が Rejected で**返る**→ガードは「手仕舞い済み」と記録して完了（届いていなければ逆指値なしの建玉が
    // 巡回対象から外れ、届いていれば偽 ID の記録が残る）。是正後: 予約を据え置き、完了を主張しない。
    [Fact]
    public async Task 実アダプタ経由_ガードの成行の応答が_TimeOut_なら_完了を主張せず撃ち直しもしない_否定形()
    {
        var client = new NonSuccessReplyTradeClient(MoomooRetType.TimeOut, string.Empty)
        {
            OnlyKind = MoomooOrderKind.Market,
            OtherRetType = MoomooRetType.Failed,
        };
        var adapter = new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate);
        var stops = new InMemoryProtectiveStopOrderStore();
        var stop = new ProtectiveStopOrder(
            Guid.NewGuid(), Guid.NewGuid(), "stop-1", "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, Attempt: 1,
            ProtectiveStopState.Active, Now.AddMinutes(-5), Now.AddMinutes(-5));
        stops.Save(stop);
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var guard = new ProtectiveStopGuard(adapter, adapter, stops, store, reservations, new FakeClock());

        var first = await guard.RunOnceAsync(10);
        await guard.RunOnceAsync(10);
        await guard.RunOnceAsync(10);

        client.Sent.Count(k => k == MoomooOrderKind.Market).Should().Be(1, "届いたか不明の成行を撃ち直さない");
        var closeDecisionId = ProtectiveStopIds.CloseDecisionId(stop.EntryDecisionId, attempt: 2);
        store.FindByDecisionId(closeDecisionId).Should().BeNull(
            "是正前は偽 ID の Rejected が記録され、ガードはそれを『手仕舞い済み』として保護を完了していた");
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active, "手仕舞い済みを主張しない");
        reservations.Find(closeDecisionId)!.State.Should().Be(OrderDispatchState.Reserved);
        var lost = first.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle().Which;
        lost.Remediation.Should().Be(ProtectiveStopRemediation.CloseDispatchIndeterminate,
            "PositionClosed（手仕舞い済み）と主張しない。CloseIntent を運び、台帳に処理中の決済として押さえさせる");
        lost.CloseIntent.Should().NotBeNull();
    }
}
