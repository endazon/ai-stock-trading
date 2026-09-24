using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;
using OrderExecutionService.Infrastructure.Persistence;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// 🔴 T-10-823〜T-10-827・T-10-829・T-10-832, FR-05, FR-10, UC-06, #876, IADR-0398:
// **見送った承認（DecisionId）は、同じ OrderApproved が再配送されても発注しない。**
//
// 是正前の穴（IADR-0356 の残余リスク 3）: 見送りを受けたリスク管理は取引台帳の承認を終端にし、
// 「処理中の決済」から外す（在庫を戻す）。ところが発注執行側では、
//   - 接続確立の失敗（確実に未発注）は予約行を**削除**しており、
//   - 予約の前に見送る理由（建玉照会の不明など）は予約行を**そもそも作らず**、
// 見送ったという事実が発注執行のどこにも残らなかった。重複配送された同じ承認は予約を取り直して
// **本物の決済注文を出し得た**。台帳の終端は単調で戻らないため、その決済は処理中に数えられず、
// 利用者の 2 本目の手仕舞いが通る＝**同じ株数に 2 本の決済が並ぶ（二重決済でショート化）**。
public class OrderExecutionServiceForgoneReplayTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 6, 0, 0, TimeSpan.Zero);

    // 進められる時計（抑止した再配送が見送りの記録時刻を動かさないことを測る）。
    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
    }

    // 接続の可否と建玉照会の結果を途中で切り替えられるブローカー（OpenD の再起動→復帰を模す）。
    // 送信の回数と建玉照会の回数を数える。
    private sealed class SwitchableBroker(BrokerProvider provider = BrokerProvider.MoomooSimulate)
        : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public bool Available { get; set; }

        // null＝照会不能（不明）。空列＝ブローカーは 1 株も持っていない。
        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = [Held(1_000)];

        public int PlaceCount { get; private set; }

        public int PositionQueryCount { get; private set; }

        public BrokerProvider Provider => provider;

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default)
        {
            PositionQueryCount++;
            return Task.FromResult(Positions);
        }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) => Place(intent);

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            Place(closeIntent, OrderStatus.Accepted);

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default) => Place(closeIntent);

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;

        private Task<BrokerOrder> Place(OrderIntent intent, OrderStatus status = OrderStatus.Filled)
        {
            if (!Available)
                throw new BrokerUnavailableException("OpenD へ接続できません（テスト・接続確立の失敗）");

            PlaceCount++;
            var filled = status == OrderStatus.Filled ? intent.Quantity : 0;
            return Task.FromResult(new BrokerOrder(
                "ORD-" + PlaceCount, intent, status, filled, intent.Price, Now, Now));
        }
    }

    // 逆指値の能力を持たないブローカー（StopOrderUnsupported を作る）。送信回数だけ数える。
    private sealed class NoStopBroker : IBrokerAdapter
    {
        public int PlaceCount { get; private set; }

        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            PlaceCount++;
            return Task.FromResult(new BrokerOrder(
                "ORD-" + PlaceCount, intent, OrderStatus.Filled, intent.Quantity, intent.Price, Now, Now));
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static BrokerPositionSnapshot Held(int signedQuantity) =>
        new("AAPL", Market.UnitedStates, signedQuantity, 100m);

    private static OrderIntent Intent(PositionEffect effect, decimal? stopLoss = 95m) =>
        new("AAPL", Market.UnitedStates, effect == PositionEffect.Close ? TradeSide.Sell : TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 100, 100m, effect,
            StopLossPrice: effect == PositionEffect.Open ? stopLoss : null);

    private static OrderApproved Approved(
        PositionEffect effect,
        decimal? stopLoss = 95m,
        StopLossExecutionMethod method = StopLossExecutionMethod.BrokerStopOrder)
    {
        var intent = Intent(effect, stopLoss);
        return new OrderApproved(Guid.NewGuid(), intent, intent.Quantity, Now, StopLossMethod: method);
    }

    private static (AppSvc Service, InMemoryExecutedOrderStore Store, InMemoryOrderReservationStore Reservations)
        NewService(IBrokerAdapter broker, IClock? clock = null)
    {
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        return (new AppSvc(broker, store, reservations, clock ?? new MutableClock(), null, null,
            broker as IBrokerPositionSource), store, reservations);
    }

    // 🔴 T-10-823（否定形・最重要）: 接続確立の失敗（確実に未発注）で見送った承認が、
    // **OpenD の復帰後に重複配送されても発注されない**。#876 の再現順序そのもの。
    [Theory]
    [InlineData(PositionEffect.Close)]
    [InlineData(PositionEffect.Open)]
    public async Task 接続失敗で見送った承認は復帰後に再配送されても発注しない_否定形(PositionEffect effect)
    {
        var broker = new SwitchableBroker { Available = false };
        var (service, store, _) = NewService(broker);
        var approved = Approved(effect);

        var first = await service.ExecuteAsync(approved);
        first.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.BrokerUnavailable);

        broker.Available = true; // OpenD が復帰した
        var replay = await service.ExecuteAsync(approved); // 同じ OrderApproved の重複配送

        broker.PlaceCount.Should().Be(0, "見送りを告げた承認を後から実発注すると、台帳が押さえていない決済が生きる");
        replay.Executed.Should().BeNull("発注していないので発注結果を作らない");
        store.GetAll().Should().BeEmpty();
    }

    // 🔴 T-10-824（否定形・最重要）: 予約を取る**前**に見送った決済（建玉照会の不明・ブローカーに建玉が無い）も、
    // 照会が回復した後の重複配送で発注されない。台帳はこの 2 理由でも在庫を戻す（確実に未発注の allowlist）ため、
    // 接続確立の失敗と同じ穴を持っていた。
    [Theory]
    [InlineData(true)]   // 照会不能（null）→ BrokerPositionsIndeterminate
    [InlineData(false)]  // 空列（0 株）→ BrokerPositionAbsent
    public async Task 予約前に見送った決済は照会が回復した後に再配送されても発注しない_否定形(bool indeterminate)
    {
        var broker = new SwitchableBroker { Available = true, Positions = indeterminate ? null : [] };
        var (service, store, _) = NewService(broker);
        var approved = Approved(PositionEffect.Close);

        var first = await service.ExecuteAsync(approved);
        first.Forgone!.Reason.Should().Be(
            indeterminate
                ? OrderDispatchForgoneReason.BrokerPositionsIndeterminate
                : OrderDispatchForgoneReason.BrokerPositionAbsent);

        broker.Positions = [Held(1_000)]; // 照会が回復し、建玉も見えるようになった
        var replay = await service.ExecuteAsync(approved);

        broker.PlaceCount.Should().Be(0);
        replay.Executed.Should().BeNull();
        store.GetAll().Should().BeEmpty();
    }

    // 🔴 T-10-825: 抑止した再配送は**ブローカーに一切触れない**（建玉照会もしない）・見送りを再発行しない・
    // 見送りの記録（状態と時刻）を動かさない。3 つの形（発注した／見送った／抑止した）を取り違えない。
    [Fact]
    public async Task 抑止した再配送は建玉照会もせず見送りの記録を動かさない()
    {
        var clock = new MutableClock();
        var broker = new SwitchableBroker { Available = false };
        var (service, _, reservations) = NewService(broker, clock);
        var approved = Approved(PositionEffect.Close);

        (await service.ExecuteAsync(approved)).Forgone.Should().NotBeNull();
        var queriesAfterFirst = broker.PositionQueryCount;
        var recordedAt = reservations.Find(approved.DecisionId)!.CompletedAt;

        broker.Available = true;
        clock.UtcNow = Now.AddSeconds(42); // 共通再試行の窓の中の再配送
        var replay = await service.ExecuteAsync(approved);

        replay.ForgoneReplaySuppressed.Should().BeTrue();
        replay.Executed.Should().BeNull();
        replay.Forgone.Should().BeNull("見送りの理由を記録していないので、見送りを再発行しない");
        broker.PositionQueryCount.Should().Be(queriesAfterFirst, "抑止はブローカーに触れる前に決まる");
        var after = reservations.Find(approved.DecisionId)!;
        after.State.Should().Be(OrderDispatchState.Forgone);
        after.CompletedAt.Should().Be(recordedAt).And.Be(Now, "記録の時刻は最初の見送りのまま");
    }

    // 🔴 T-10-826: 予約前の見送りは、どの理由でも**見送りを返す前に** Forgone を記録している
    // （見送りの結果が呼び出し側へ戻った時点で、記録はもうコミット済み＝発行より先）。
    // 到達させやすい全通り: 損切り価格なし／逆指値の能力なし／手法が許されない／建玉なし／建玉照会の不明。
    public static TheoryData<string> PreReservationScenarios() =>
    [
        nameof(OrderDispatchForgoneReason.StopLossPriceMissing),
        nameof(OrderDispatchForgoneReason.StopOrderUnsupported),
        nameof(OrderDispatchForgoneReason.StopLossMethodNotPermitted),
        nameof(OrderDispatchForgoneReason.BrokerPositionAbsent),
        nameof(OrderDispatchForgoneReason.BrokerPositionsIndeterminate),
    ];

    [Theory]
    [MemberData(nameof(PreReservationScenarios))]
    public async Task 予約前の見送りはどの理由でも見送りを返す前にForgoneを記録する(string reasonName)
    {
        var reason = Enum.Parse<OrderDispatchForgoneReason>(reasonName);
        IBrokerAdapter broker = reason switch
        {
            OrderDispatchForgoneReason.StopOrderUnsupported => new NoStopBroker(),
            OrderDispatchForgoneReason.StopLossMethodNotPermitted => new SwitchableBroker(BrokerProvider.MoomooReal),
            OrderDispatchForgoneReason.BrokerPositionAbsent => new SwitchableBroker { Positions = [] },
            OrderDispatchForgoneReason.BrokerPositionsIndeterminate => new SwitchableBroker { Positions = null },
            _ => new SwitchableBroker(),
        };
        var approved = reason switch
        {
            OrderDispatchForgoneReason.StopLossPriceMissing => Approved(PositionEffect.Open, stopLoss: null),
            OrderDispatchForgoneReason.StopOrderUnsupported => Approved(PositionEffect.Open),
            OrderDispatchForgoneReason.StopLossMethodNotPermitted =>
                Approved(PositionEffect.Open, method: StopLossExecutionMethod.NoProtectiveStop),
            _ => Approved(PositionEffect.Close),
        };
        var (service, _, reservations) = NewService(broker);

        var result = await service.ExecuteAsync(approved);

        result.Forgone!.Reason.Should().Be(reason, "前提: 狙った理由で見送っている");
        var marker = reservations.Find(approved.DecisionId);
        marker.Should().NotBeNull("見送りを告げる前に、この DecisionId をもう送らないことを記録する");
        marker!.State.Should().Be(OrderDispatchState.Forgone);
        marker.BrokerOrderId.Should().BeNull("注文は存在しない（注文 ID を捏造しない）");
        marker.CompletedAt.Should().Be(Now);
    }

    // 🔴 T-10-827（否定形・Principle A）: **別の配送が同じ DecisionId の予約（Reserved＝送ったか不明）を持っているとき、
    // 予約前の見送りは見送りを主張しない。** 主張すると台帳が「処理中の決済」から外し、並行して送られているかもしれない
    // 決済の押さえが解ける。従来の予約競合と同じ例外で止め、予約は Reserved のまま触らない。
    [Fact]
    public async Task 別の配送が予約を持つDecisionIdでは予約前の見送りを主張しない_否定形()
    {
        var broker = new SwitchableBroker { Available = true, Positions = null }; // この配送では照会が不明
        var (service, _, reservations) = NewService(broker);
        var approved = Approved(PositionEffect.Close);
        reservations.TryReserve(approved.DecisionId, Now.AddSeconds(-1)).Should().BeTrue("前提: 並行した配送が発注に着手済み");

        var dispatch = async () => await service.ExecuteAsync(approved);

        await dispatch.Should().ThrowAsync<OrderDispatchReservationConflictException>();
        var reservation = reservations.Find(approved.DecisionId)!;
        reservation.State.Should().Be(OrderDispatchState.Reserved, "送ったか不明の予約を見送りへ書き換えない");
        reservation.CompletedAt.Should().BeNull();
        broker.PlaceCount.Should().Be(0);
    }

    // 🔴 T-10-829（否定形）: 確定済み（Completed＝発注済み）の予約がある DecisionId でも見送りを主張しない。
    // 発注結果の記録（相 1）が無い状態は本来到達しないが、到達したときに「見送り」と告げる側へは倒さない。
    [Fact]
    public async Task 確定済みの予約があるDecisionIdでは見送りを主張しない_否定形()
    {
        var broker = new SwitchableBroker { Available = true, Positions = null };
        var (service, _, reservations) = NewService(broker);
        var approved = Approved(PositionEffect.Close);
        reservations.TryReserve(approved.DecisionId, Now.AddMinutes(-1));
        reservations.MarkCompleted(approved.DecisionId, "BRK-1", Now.AddMinutes(-1));

        var dispatch = async () => await service.ExecuteAsync(approved);

        var thrown = await dispatch.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Should().NotBeOfType<OrderDispatchReservationConflictException>(
            "予約競合（送ったか不明）ではなく、確定済みを見送りと書き換えないための停止である");
        reservations.Find(approved.DecisionId)!.State.Should().Be(OrderDispatchState.Completed);
        broker.PlaceCount.Should().Be(0);
    }

    // 🔴 T-10-832（変えない側）: 接続確立の失敗は従来どおり見送りで正常終了し、見送りを 1 件返す。
    // 予約は削除せず Forgone（注文 ID は無い）。発注に成功した承認の再配送は従来どおり相 1 が既存結果を再発行する。
    [Fact]
    public async Task 接続失敗は見送りを返し_発注できた承認の再配送は従来どおり既存結果を再発行する()
    {
        var broker = new SwitchableBroker { Available = false };
        var (service, _, reservations) = NewService(broker);
        var forgoneApproval = Approved(PositionEffect.Close);

        var forgone = await service.ExecuteAsync(forgoneApproval);

        forgone.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.BrokerUnavailable);
        forgone.ForgoneReplaySuppressed.Should().BeFalse("初回は見送りそのもの（抑止ではない）");
        var marker = reservations.Find(forgoneApproval.DecisionId)!;
        marker.State.Should().Be(OrderDispatchState.Forgone);
        marker.BrokerOrderId.Should().BeNull();

        broker.Available = true;
        var placedApproval = Approved(PositionEffect.Close);
        var placed = await service.ExecuteAsync(placedApproval);
        var again = await service.ExecuteAsync(placedApproval);

        placed.Executed!.OrderId.Should().Be("ORD-1");
        again.Executed!.OrderId.Should().Be("ORD-1", "発注済みの再配送は相 1 が既存結果を再発行する（変えない側）");
        again.ForgoneReplaySuppressed.Should().BeFalse();
        broker.PlaceCount.Should().Be(1);
        reservations.Find(placedApproval.DecisionId)!.State.Should().Be(OrderDispatchState.Completed);
    }
}
