using AiStockTrading.OrderExecution.Application.Adapters;
using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;
using FluentAssertions;
using Xunit;
using AppSvc = AiStockTrading.OrderExecution.Application.Services.OrderExecutionService;

namespace AiStockTrading.OrderExecution.Application.Tests;

// FR-05, UC-01, UC-02, IADR-0007/0016: 発注執行（承認→発注→約定確定・スリッページ記録）の検証。
public class OrderExecutionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 6, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // 任意の BrokerOrder を返すテスト用ブローカ（スリッページ等の制御用）。発注回数を数える。
    private sealed class FakeBroker(BrokerOrder order) : IBrokerAdapter
    {
        public int PlaceCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            PlaceCount++;
            return Task.FromResult(order);
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default)
            => Task.FromResult<BrokerOrder?>(order);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static OrderApproved Approved(OrderIntent intent) =>
        new(Guid.NewGuid(), intent, intent.Quantity, Now);

    private static OrderIntent Intent(int qty = 10, decimal price = 1_000m,
        PositionEffect effect = PositionEffect.Open, TradeSide side = TradeSide.Buy) =>
        new("AAPL", Market.UnitedStates, side, ProductType.Cash, TradeMode.Paper, qty, price, effect);

    [Fact]
    public async Task 承認注文はペーパーで約定しOrderExecutedを返す()
    {
        var store = new InMemoryExecutedOrderStore();
        var service = new AppSvc(new PaperBrokerAdapter(), store, new FakeClock());
        var approved = Approved(Intent());

        var executed = await service.ExecuteAsync(approved);

        executed.Status.Should().Be(OrderStatus.Filled);
        executed.FilledQuantity.Should().Be(10);
        executed.AveragePrice.Should().Be(1_000m);
        executed.DecisionId.Should().Be(approved.DecisionId);
        store.GetAll().Should().ContainSingle(r => r.DecisionId == approved.DecisionId);
    }

    [Fact]
    public async Task ブローカ拒否はOrderExecutedのRejectedになる()
    {
        // IADR-0007: 数量 0 は実ブローカが拒否する不正注文 → Rejected（発注前拒否 OrderRejected とは別）。
        var store = new InMemoryExecutedOrderStore();
        var service = new AppSvc(new PaperBrokerAdapter(), store, new FakeClock());

        var executed = await service.ExecuteAsync(Approved(Intent(qty: 0)));

        executed.Status.Should().Be(OrderStatus.Rejected);
        executed.FilledQuantity.Should().Be(0);
        store.GetAll().Single().SlippageRatio.Should().Be(0m); // 未約定はスリッページ 0
    }

    [Fact]
    public async Task Close注文も同一経路で約定する()
    {
        var store = new InMemoryExecutedOrderStore();
        var service = new AppSvc(new PaperBrokerAdapter(), store, new FakeClock());

        var executed = await service.ExecuteAsync(Approved(Intent(effect: PositionEffect.Close, side: TradeSide.Sell)));

        executed.Status.Should().Be(OrderStatus.Filled);
        store.GetAll().Single().PositionEffect.Should().Be(PositionEffect.Close);
    }

    [Fact]
    public async Task スリッページが取引毎に算出され記録される()
    {
        // 計画 1000 の買いが 1005 で約定 → +0.5% の不利スリッページ。
        var store = new InMemoryExecutedOrderStore();
        var intent = Intent(price: 1_000m);
        var brokerOrder = new BrokerOrder("o1", intent, OrderStatus.Filled, 10, 1_005m, Now, Now);
        var service = new AppSvc(new FakeBroker(brokerOrder), store, new FakeClock());

        await service.ExecuteAsync(Approved(intent));

        store.GetAll().Single().SlippageRatio.Should().Be(0.005m);
    }

    [Fact]
    public async Task 同一DecisionIdの再処理では再発注せず既存結果を返す()
    {
        // 冪等性: MassTransit 再配送で同一 OrderApproved が再処理されても二重発注・二重計上しない。
        var store = new InMemoryExecutedOrderStore();
        var intent = Intent();
        var broker = new FakeBroker(new BrokerOrder("o1", intent, OrderStatus.Filled, 10, 1_000m, Now, Now));
        var service = new AppSvc(broker, store, new FakeClock());
        var approved = Approved(intent);

        var first = await service.ExecuteAsync(approved);
        var second = await service.ExecuteAsync(approved); // 再処理

        broker.PlaceCount.Should().Be(1);          // 再発注しない
        store.GetAll().Should().ContainSingle();    // 二重計上しない
        second.OrderId.Should().Be(first.OrderId);  // 既存結果を返す
    }
}
