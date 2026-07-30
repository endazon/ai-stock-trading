using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.OrderExecution.Worker.Composable.Adapters;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.OrderExecution.Worker.Tests;

// #141, FR-05, IADR-0092: 実照会プローブの3値写像を fake client で検証する（実 OpenD 不使用）。
// at-most-once の要＝「不明は解放しない」を単体で固定する。実 OpenD 照会（MMApiMoomooTradeClient）は live 検証。
public class MoomooReservationBrokerProbeTests
{
    private static readonly DateTimeOffset ReservedAt = new(2026, 7, 17, 6, 0, 0, TimeSpan.Zero);

    // FindOrderByClientIdAsync の戻り/例外を制御する fake。契約: 一致→snapshot / 確実に不在→null / 照会失敗→例外。
    private sealed class FakeClient : IMoomooTradeClient
    {
        public MoomooOrderSnapshot? Snapshot { get; set; }
        public Func<Exception>? Throw { get; set; }
        public string? SeenClientOrderId { get; private set; }
        public DateTimeOffset? SeenReservedAt { get; private set; }

        public Task<MoomooOrderSnapshot?> FindOrderByClientIdAsync(
            string clientOrderId, DateTimeOffset reservedAtUtc, CancellationToken cancellationToken = default)
        {
            SeenClientOrderId = clientOrderId;
            SeenReservedAt = reservedAtUtc;
            if (Throw is not null) throw Throw();
            return Task.FromResult(Snapshot);
        }

        public Task<IReadOnlyList<MoomooPositionSnapshot>> GetPositionsAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("予約照合は建玉照会を用いない。");

        // 本テストで未使用（発注/取消/単純照会）。
        public Task<MoomooOrderResult> PlaceOrderAsync(MoomooOrderRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<MoomooOrderResult?> QueryOrderAsync(string orderId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static OrderDispatchReservation Reservation(Guid decisionId) =>
        new(decisionId, OrderDispatchState.Reserved, ReservedAt, BrokerOrderId: null);

    private static MoomooOrderSnapshot Snapshot(MoomooOrderState state = MoomooOrderState.FilledAll) =>
        new("mo-77", state, "AAPL", MoomooMarket.UnitedStates, MoomooSide.Buy,
            Quantity: 10, Price: 100m, FilledQuantity: 10, AveragePrice: 101m,
            PlacedAt: ReservedAt, CompletedAt: ReservedAt.AddSeconds(5));

    [Fact]
    public async Task 一致注文が見つかれば_Placed_で_BrokerOrder_を再構成する()
    {
        // 受け入れ基準1（発注済み→確定）: remark 一致注文が見つかる → Placed。intent/状態/約定が再構成される。
        var client = new FakeClient { Snapshot = Snapshot() };
        var probe = new MoomooReservationBrokerProbe(client);
        var decisionId = Guid.NewGuid();

        var result = await probe.ProbeAsync(Reservation(decisionId));

        result.Outcome.Should().Be(ReservationProbeOutcome.Placed);
        result.Order.Should().NotBeNull();
        result.Order!.OrderId.Should().Be("mo-77");
        result.Order.Status.Should().Be(OrderStatus.Filled);
        result.Order.FilledQuantity.Should().Be(10);
        result.Order.AveragePrice.Should().Be(101m);
        result.Order.Intent.Symbol.Should().Be("AAPL");
        result.Order.Intent.Market.Should().Be(Market.UnitedStates);
        result.Order.Intent.Side.Should().Be(TradeSide.Buy);
        result.Order.Intent.Quantity.Should().Be(10);
        result.Order.Intent.Price.Should().Be(100m);
        // 突合キーは DecisionId("N")・履歴窓は ReservedAt。
        client.SeenClientOrderId.Should().Be(decisionId.ToString("N"));
        client.SeenReservedAt.Should().Be(ReservedAt);
    }

    [Fact]
    public async Task 未約定でも注文が存在すれば_Placed_で終端化する()
    {
        // 非終端（Submitted/PartiallyFilled）でも「注文が存在する＝発注済み」→ 再発注しない（Placed）。
        var client = new FakeClient { Snapshot = Snapshot(MoomooOrderState.FilledPart) };
        var probe = new MoomooReservationBrokerProbe(client);

        var result = await probe.ProbeAsync(Reservation(Guid.NewGuid()));

        result.Outcome.Should().Be(ReservationProbeOutcome.Placed);
        result.Order!.Status.Should().Be(OrderStatus.PartiallyFilled);
    }

    [Fact]
    public async Task 全列挙で一致ゼロなら_NotPlaced_を返す()
    {
        // 受け入れ基準1（未発注→解放）: client が null（＝全市場・現在＋履歴を成功裏に列挙して一致ゼロ）→ 確実に未発注。
        var client = new FakeClient { Snapshot = null };
        var probe = new MoomooReservationBrokerProbe(client);

        var result = await probe.ProbeAsync(Reservation(Guid.NewGuid()));

        result.Outcome.Should().Be(ReservationProbeOutcome.NotPlaced);
        result.Order.Should().BeNull();
    }

    [Fact]
    public async Task 照会失敗は_Indeterminate_に倒す_fail_safe()
    {
        // 受け入れ基準2（fail-safe）: 照会不達・応答異常は「不明」。解放も終端化もしない（二重発注を招かない）。
        var client = new FakeClient { Throw = () => new InvalidOperationException("OpenD 不達") };
        var probe = new MoomooReservationBrokerProbe(client);

        var result = await probe.ProbeAsync(Reservation(Guid.NewGuid()));

        result.Outcome.Should().Be(ReservationProbeOutcome.Indeterminate);
        result.Order.Should().BeNull();
    }

    [Fact]
    public async Task キャンセルは握りつぶさず伝播する()
    {
        // シャットダウン等のキャンセルは Indeterminate へ倒さず素通しする（巡回の停止を妨げない）。
        var client = new FakeClient { Throw = () => new OperationCanceledException() };
        var probe = new MoomooReservationBrokerProbe(client);

        var act = async () => await probe.ProbeAsync(Reservation(Guid.NewGuid()));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task 日本市場_売り注文も正しく再構成する()
    {
        var client = new FakeClient
        {
            Snapshot = new MoomooOrderSnapshot("mo-jp", MoomooOrderState.FilledAll, "7203",
                MoomooMarket.Japan, MoomooSide.Sell, 100, 2500m, 100, 2499m, ReservedAt, ReservedAt),
        };
        var probe = new MoomooReservationBrokerProbe(client);

        var result = await probe.ProbeAsync(Reservation(Guid.NewGuid()));

        result.Order!.Intent.Market.Should().Be(Market.Japan);
        result.Order.Intent.Side.Should().Be(TradeSide.Sell);
        result.Order.Intent.Symbol.Should().Be("7203");
    }
}
