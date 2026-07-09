using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests;

// FR-12: ペーパートレードモード（実発注せず、判断・記録・報告のフローは実発注と同一）
public class PaperBrokerAdapterTests
{
    private static OrderIntent Intent(int quantity = 10, decimal price = 3000m) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper, quantity, price);

    [Fact]
    public async Task 発注すると現在値で即時全量約定する()
    {
        var broker = new PaperBrokerAdapter();

        var order = await broker.PlaceOrderAsync(Intent(quantity: 10, price: 3000m));

        order.OrderId.Should().NotBeNullOrWhiteSpace();
        order.Status.Should().Be(OrderStatus.Filled);
        order.FilledQuantity.Should().Be(10);
        order.AveragePrice.Should().Be(3000m);
    }

    [Fact]
    public async Task 発注済み注文を注文IDで照会できる()
    {
        var broker = new PaperBrokerAdapter();
        var placed = await broker.PlaceOrderAsync(Intent());

        var fetched = await broker.GetOrderAsync(placed.OrderId);

        fetched.Should().NotBeNull();
        fetched!.OrderId.Should().Be(placed.OrderId);
        fetched.Status.Should().Be(OrderStatus.Filled);
    }

    [Fact]
    public async Task 未知の注文IDの照会はnullを返す()
    {
        var broker = new PaperBrokerAdapter();

        var fetched = await broker.GetOrderAsync("unknown-order-id");

        fetched.Should().BeNull();
    }

    [Fact]
    public async Task 約定済み注文の取消は失敗する()
    {
        var broker = new PaperBrokerAdapter();
        var placed = await broker.PlaceOrderAsync(Intent());

        var act = () => broker.CancelOrderAsync(placed.OrderId);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(0, 3000)]   // 数量ゼロ
    [InlineData(-5, 3000)]  // 数量が負
    [InlineData(10, 0)]     // 価格ゼロ
    [InlineData(10, -1)]    // 価格が負
    public async Task 数量や価格が不正な注文は約定せず証券会社拒否になる(int quantity, decimal price)
    {
        // Issue #30 / FR-05, FR-12: 実ブローカーが拒否する不正注文はペーパーでも約定させない。
        var broker = new PaperBrokerAdapter();

        var order = await broker.PlaceOrderAsync(Intent(quantity: quantity, price: price));

        order.Status.Should().Be(OrderStatus.Rejected);
        order.FilledQuantity.Should().Be(0);
        order.AveragePrice.Should().Be(0m);
    }

    [Fact]
    public async Task 証券会社拒否の注文は取消できない()
    {
        // Issue #30: Rejected は終端状態。取消は失敗する。
        var broker = new PaperBrokerAdapter();
        var rejected = await broker.PlaceOrderAsync(Intent(quantity: 0));

        var act = () => broker.CancelOrderAsync(rejected.OrderId);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
