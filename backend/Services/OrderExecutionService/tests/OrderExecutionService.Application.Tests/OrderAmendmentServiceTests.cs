using AiStockTrading.OrderExecution.Application.Adapters;
using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.OrderExecution.Application.Services;
using AiStockTrading.OrderExecution.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;
using FluentAssertions;
using Xunit;
using AppSvc = AiStockTrading.OrderExecution.Application.Services.OrderExecutionService;

namespace AiStockTrading.OrderExecution.Application.Tests;

// FR-05, FR-19, #154, IADR-0067: 注文の訂正・取消（ブローカ適用＋永続化＋イベント生成）の検証。
// 本サービスは配管であり、訂正・取消を「起こす」駆動元（時限取消・#141 リコンサイル・#152 pause 強制取消）は
// 対象外（各 issue に残す）。イベントの発行そのものは Worker 層の OrderAmendmentDispatcher が行う。
public class OrderAmendmentServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 6, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private static OrderIntent Intent(int quantity = 10, decimal price = 3000m) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper, quantity, price);

    // 非終端の注文を1件発注済みにした状態を作る（immediateFill=false・IADR-0067）。
    private sealed class Fixture
    {
        public PaperBrokerAdapter Broker { get; } = new(immediateFill: false);

        public InMemoryExecutedOrderStore ExecutedOrders { get; } = new();

        public InMemoryOrderLifecycleStore Lifecycle { get; } = new();

        public OrderAmendmentService Service => new(Broker, Broker, ExecutedOrders, Lifecycle, new FakeClock());

        public async Task<(Guid DecisionId, string OrderId)> PlaceAsync(int quantity = 10, decimal price = 3000m)
        {
            var decisionId = Guid.NewGuid();
            var intent = Intent(quantity, price);
            var execution = new AppSvc(
                Broker, ExecutedOrders, new InMemoryOrderReservationStore(), new FakeClock());
            var executed = await execution.ExecuteAsync(
                new Shared.Contracts.Events.OrderApproved(decisionId, intent, quantity, Now));
            return (decisionId, executed.OrderId);
        }
    }

    [Fact]
    public async Task 取消するとブローカへ適用され台帳へ追記されイベントを返す()
    {
        var fx = new Fixture();
        var (decisionId, orderId) = await fx.PlaceAsync();

        var cancelled = await fx.Service.CancelAsync(decisionId, reason: "時限取消");

        cancelled.DecisionId.Should().Be(decisionId);
        cancelled.OrderId.Should().Be(orderId);
        cancelled.Reason.Should().Be("時限取消");
        cancelled.CancelledAt.Should().Be(Now);

        // ブローカに反映されている。
        var order = await fx.Broker.GetOrderAsync(orderId);
        order!.Status.Should().Be(OrderStatus.Cancelled);

        // 永続化されている。
        var appended = fx.Lifecycle.GetByOrderId(orderId);
        appended.Should().ContainSingle();
        appended[0].Kind.Should().Be(OrderLifecycleKind.Cancelled);
        appended[0].DecisionId.Should().Be(decisionId);
        appended[0].Reason.Should().Be("時限取消");
    }

    [Fact]
    public async Task 訂正するとブローカへ適用され訂正前後の値つきで台帳へ追記されイベントを返す()
    {
        var fx = new Fixture();
        var (decisionId, orderId) = await fx.PlaceAsync(quantity: 10, price: 3000m);

        var modified = await fx.Service.ModifyAsync(decisionId, quantity: 4, price: 2950m, reason: "数量縮小");

        modified.DecisionId.Should().Be(decisionId);
        modified.OrderId.Should().Be(orderId);
        modified.PreviousQuantity.Should().Be(10);
        modified.PreviousPrice.Should().Be(3000m);
        modified.Quantity.Should().Be(4);
        modified.Price.Should().Be(2950m);
        modified.ModifiedAt.Should().Be(Now);

        var order = await fx.Broker.GetOrderAsync(orderId);
        order!.Intent.Quantity.Should().Be(4);
        order.Intent.Price.Should().Be(2950m);

        var appended = fx.Lifecycle.GetByOrderId(orderId);
        appended.Should().ContainSingle();
        appended[0].Kind.Should().Be(OrderLifecycleKind.Modified);
        appended[0].PreviousQuantity.Should().Be(10);
        appended[0].Quantity.Should().Be(4);
    }

    [Fact]
    public async Task 連続訂正では直前の内容が訂正前の値になる()
    {
        // 訂正前の値はブローカの現在値を権威とする（executed_orders は発注時の値のまま更新しないため、
        // そちらを使うと2回目以降の訂正で誤った PreviousQuantity を報告してしまう）。
        var fx = new Fixture();
        var (decisionId, _) = await fx.PlaceAsync(quantity: 10, price: 3000m);

        await fx.Service.ModifyAsync(decisionId, quantity: 8, price: 2980m, reason: "1回目");
        var second = await fx.Service.ModifyAsync(decisionId, quantity: 6, price: 2960m, reason: "2回目");

        second.PreviousQuantity.Should().Be(8);
        second.PreviousPrice.Should().Be(2980m);
        second.Quantity.Should().Be(6);
    }

    [Fact]
    public async Task 未知の判断IDの取消は失敗する()
    {
        var fx = new Fixture();

        var act = () => fx.Service.CancelAsync(Guid.NewGuid(), reason: "不明");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ブローカ適用に失敗したら永続化しない()
    {
        // 不整合（ブローカ未適用なのに取消済みの記録が残る）を作らないため、ブローカ適用の後に永続化する。
        var fx = new Fixture();
        var (decisionId, orderId) = await fx.PlaceAsync();
        await fx.Service.CancelAsync(decisionId, reason: "1回目");

        // 既に取消済み（終端）の注文への再取消はブローカが拒否する。
        var act = () => fx.Service.CancelAsync(decisionId, reason: "2回目");

        await act.Should().ThrowAsync<InvalidOperationException>();
        fx.Lifecycle.GetByOrderId(orderId).Should().ContainSingle("ブローカ適用に失敗した取消は記録しない");
    }

    [Fact]
    public async Task 約定済み注文の訂正は失敗し永続化しない()
    {
        // 既定の即時約定（immediateFill=true）で発注した注文は終端のため訂正できない。
        var broker = new PaperBrokerAdapter();
        var store = new InMemoryExecutedOrderStore();
        var lifecycle = new InMemoryOrderLifecycleStore();
        var decisionId = Guid.NewGuid();
        var execution = new AppSvc(broker, store, new InMemoryOrderReservationStore(), new FakeClock());
        var executed = await execution.ExecuteAsync(
            new Shared.Contracts.Events.OrderApproved(decisionId, Intent(), 10, Now));

        var service = new OrderAmendmentService(broker, broker, store, lifecycle, new FakeClock());
        var act = () => service.ModifyAsync(decisionId, quantity: 5, price: 2900m, reason: "遅れた訂正");

        await act.Should().ThrowAsync<InvalidOperationException>();
        lifecycle.GetByOrderId(executed.OrderId).Should().BeEmpty();
    }
}
