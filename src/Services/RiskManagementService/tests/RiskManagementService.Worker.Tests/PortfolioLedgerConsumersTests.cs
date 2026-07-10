using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Worker.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.RiskManagement.Worker.Tests;

// FR-10, FR-05, IADR-0018: OrderApproved/OrderExecuted を購読して取引台帳へ射影する Consumer を
// MassTransit テストハーネス + インメモリ台帳で検証する。
public class PortfolioLedgerConsumersTests
{
    private static OrderIntent BuyIntent(int qty, decimal price) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper, qty, price);

    private static ServiceProvider BuildProvider(InMemoryPortfolioLedgerStore ledger) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IPortfolioLedgerStore>(ledger)
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<OrderApprovedLedgerConsumer>();
                x.AddConsumer<OrderExecutedLedgerConsumer>();
            })
            .BuildServiceProvider(true);

    [Fact]
    public async Task 承認から約定までを購読し台帳へ射影する()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        await using var provider = BuildProvider(ledger);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var decisionId = Guid.NewGuid();
        await harness.Bus.Publish(new OrderApproved(decisionId, BuyIntent(10, 1_000m), 10, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<OrderApproved>()).Should().BeTrue();

        await harness.Bus.Publish(new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.Filled, 10, 1_050m, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<OrderExecuted>()).Should().BeTrue();

        var fills = ledger.GetFills();
        fills.Should().HaveCount(1);
        fills[0].Symbol.Should().Be("AAPL");
        fills[0].Quantity.Should().Be(10);
        fills[0].Price.Should().Be(1_050m);

        await harness.Stop();
    }

    [Fact]
    public async Task 約定していない結果は台帳に載せない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        await using var provider = BuildProvider(ledger);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var decisionId = Guid.NewGuid();
        await harness.Bus.Publish(new OrderApproved(decisionId, BuyIntent(10, 1_000m), 10, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<OrderApproved>()).Should().BeTrue();

        // 取消（Cancelled）は約定でないため台帳に載らない。
        await harness.Bus.Publish(new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.Cancelled, 0, 0m, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<OrderExecuted>()).Should().BeTrue();

        ledger.GetFills().Should().BeEmpty();

        await harness.Stop();
    }
}
