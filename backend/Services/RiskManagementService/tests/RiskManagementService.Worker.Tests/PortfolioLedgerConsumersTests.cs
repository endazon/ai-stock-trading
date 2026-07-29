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

    // #270, IADR-0112: moomoo は Accepted（約定 0）→ 部分約定 → 全量約定と非同期に遷移する。
    // 約定があれば（全量を待たずに）台帳へ載せ、同一 OrderId は累積値で単調に更新する。
    [Fact]
    public async Task 部分約定は約定時点で台帳に載り全量約定で累積値へ更新される()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        await using var provider = BuildProvider(ledger);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var decisionId = Guid.NewGuid();
        await harness.Bus.Publish(new OrderApproved(decisionId, BuyIntent(1_000, 340m), 1_000, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<OrderApproved>()).Should().BeTrue();

        // 発注直後（未約定）は台帳に載らない ＝ #270 の出発点。
        await harness.Bus.Publish(new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.Accepted, 0, 0m, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<OrderExecuted>()).Should().BeTrue();
        ledger.GetFills().Should().BeEmpty();

        // 部分約定: 全量を待たずに載せる（待つと次サイクルの統制が素通しになる）。
        await harness.Bus.Publish(new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.PartiallyFilled, 300, 340.5m, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<OrderExecuted>(x => x.Context.Message.FilledQuantity == 300)).Should().BeTrue();
        ledger.GetFills().Should().ContainSingle().Which.Quantity.Should().Be(300);

        // 全量約定: 累積値（差分ではない）で同一行を更新する＝二重計上しない。
        await harness.Bus.Publish(new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.Filled, 1_000, 340.8m, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<OrderExecuted>(x => x.Context.Message.FilledQuantity == 1_000)).Should().BeTrue();

        var fills = ledger.GetFills();
        fills.Should().ContainSingle();
        fills[0].Quantity.Should().Be(1_000);
        fills[0].Price.Should().Be(340.8m);

        await harness.Stop();
    }

    // #270, IADR-0112: 部分約定のまま取消・失効した注文の約定分を落とさない（従来は Status で弾いて過少計上だった）。
    [Fact]
    public async Task 部分約定のまま取消された注文も約定分が台帳に載る()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        await using var provider = BuildProvider(ledger);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var decisionId = Guid.NewGuid();
        await harness.Bus.Publish(new OrderApproved(decisionId, BuyIntent(1_000, 340m), 1_000, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<OrderApproved>()).Should().BeTrue();

        await harness.Bus.Publish(new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.Cancelled, 400, 339.9m, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<OrderExecuted>()).Should().BeTrue();

        ledger.GetFills().Should().ContainSingle().Which.Quantity.Should().Be(400);

        await harness.Stop();
    }

    // #270, IADR-0112: 再配送・巡回重複・順序前後で約定数量が巻き戻らない（単調 upsert）。
    [Fact]
    public async Task 同一注文の再送や少ない数量の後追いでは台帳が巻き戻らない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        await using var provider = BuildProvider(ledger);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var decisionId = Guid.NewGuid();
        await harness.Bus.Publish(new OrderApproved(decisionId, BuyIntent(1_000, 340m), 1_000, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<OrderApproved>()).Should().BeTrue();

        await harness.Bus.Publish(new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.Filled, 1_000, 340.8m, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<OrderExecuted>()).Should().BeTrue();

        // 遅れて届いた古いスナップショット（部分約定）。
        await harness.Bus.Publish(new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.PartiallyFilled, 300, 340.5m, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<OrderExecuted>(x => x.Context.Message.FilledQuantity == 300)).Should().BeTrue();

        var fills = ledger.GetFills();
        fills.Should().ContainSingle();
        fills[0].Quantity.Should().Be(1_000);
        fills[0].Price.Should().Be(340.8m);

        await harness.Stop();
    }
}
