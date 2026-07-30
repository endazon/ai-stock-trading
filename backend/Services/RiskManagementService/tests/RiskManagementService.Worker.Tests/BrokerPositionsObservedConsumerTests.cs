using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Worker.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.RiskManagement.Worker.Tests;

// FR-05, FR-10, FR-11, #292, IADR-0118: ブローカ建玉の観測を購読し、取引台帳との乖離を報告する Consumer。
public class BrokerPositionsObservedConsumerTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 30, 6, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => At;

        public DateOnly Today => DateOnly.FromDateTime(At.UtcDateTime);
    }

    private static ServiceProvider BuildProvider(InMemoryPortfolioLedgerStore ledger, PositionDriftTracker tracker) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IPortfolioLedgerStore>(ledger)
            .AddSingleton(tracker)
            .AddSingleton<IClock, FixedClock>()
            .AddMassTransitTestHarness(x => x.AddConsumer<BrokerPositionsObservedConsumer>())
            .BuildServiceProvider(true);

    // AAPL を 100 株保有している台帳。
    private static InMemoryPortfolioLedgerStore LedgerWithAapl(int quantity)
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var decisionId = Guid.NewGuid();
        ledger.AppendApproval(
            decisionId,
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper,
                quantity, 20m, PositionEffect.Open, StopLossPrice: null, FxRateToBase: 150m),
            At.AddDays(-1));
        ledger.AppendFill(decisionId, "open-1", quantity, 20m, At.AddDays(-1));
        return ledger;
    }

    private static BrokerPositionsObserved Observed(params BrokerPositionSnapshot[] positions) =>
        new(positions, At);

    [Fact]
    public async Task 一致していれば乖離を発行しない()
    {
        await using var provider = BuildProvider(LedgerWithAapl(100), new PositionDriftTracker());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(Observed(new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 100, 20m)));
        (await harness.Consumed.Any<BrokerPositionsObserved>()).Should().BeTrue();

        (await harness.Published.Any<PositionReconciliationDrift>()).Should().BeFalse();
    }

    [Fact]
    public async Task 一度きりの乖離では発行しない()
    {
        // 発注直後〜約定が台帳へ届くまでの一過性のズレで鳴らせない。
        await using var provider = BuildProvider(LedgerWithAapl(100), new PositionDriftTracker());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(Observed(new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 80, 20m)));
        (await harness.Consumed.Any<BrokerPositionsObserved>()).Should().BeTrue();

        (await harness.Published.Any<PositionReconciliationDrift>()).Should().BeFalse();
    }

    [Fact]
    public async Task 連続で同一の乖離なら双方の数量つきで発行する()
    {
        await using var provider = BuildProvider(LedgerWithAapl(100), new PositionDriftTracker());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var observed = Observed(new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 80, 20m));
        await harness.Bus.Publish(observed);
        await harness.Consumed.Any<BrokerPositionsObserved>();
        await harness.Bus.Publish(observed);

        (await harness.Published.Any<PositionReconciliationDrift>(c =>
            c.Context.Message.Drifts.Count == 1
            && c.Context.Message.Drifts[0].Symbol == "AAPL"
            && c.Context.Message.Drifts[0].LedgerQuantity == 100
            && c.Context.Message.Drifts[0].BrokerQuantity == 80
            && c.Context.Message.Drifts[0].Kind == PositionDriftKind.QuantityMismatch
            && c.Context.Message.ObservedAt == At
            && c.Context.Message.DetectedAt == At)).Should().BeTrue();
    }

    [Fact]
    public async Task 台帳が空でもブローカにだけある建玉を検出する()
    {
        // 手動売買・外部約定。#270 破損期の過大建玉もこの形で現れる。
        await using var provider = BuildProvider(new InMemoryPortfolioLedgerStore(), new PositionDriftTracker());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var observed = Observed(new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 4072, 20.5m));
        await harness.Bus.Publish(observed);
        await harness.Consumed.Any<BrokerPositionsObserved>();
        await harness.Bus.Publish(observed);

        (await harness.Published.Any<PositionReconciliationDrift>(c =>
            c.Context.Message.Drifts[0].Kind == PositionDriftKind.BrokerOnly
            && c.Context.Message.Drifts[0].BrokerQuantity == 4072)).Should().BeTrue();
    }

    [Fact]
    public async Task 建玉ゼロの観測で台帳側の建玉を乖離として検出する()
    {
        await using var provider = BuildProvider(LedgerWithAapl(100), new PositionDriftTracker());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(Observed());
        await harness.Consumed.Any<BrokerPositionsObserved>();
        await harness.Bus.Publish(Observed());

        (await harness.Published.Any<PositionReconciliationDrift>(c =>
            c.Context.Message.Drifts[0].Kind == PositionDriftKind.LedgerOnly
            && c.Context.Message.Drifts[0].LedgerQuantity == 100)).Should().BeTrue();
    }

    [Fact]
    public async Task 是正の発注は行わない()
    {
        // 乖離に対して自律的に発注する経路を作らない（IADR-0118）。承認イベントが 1 件も出ないことを固定する。
        await using var provider = BuildProvider(LedgerWithAapl(100), new PositionDriftTracker());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var observed = Observed(new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 80, 20m));
        await harness.Bus.Publish(observed);
        await harness.Consumed.Any<BrokerPositionsObserved>();
        await harness.Bus.Publish(observed);
        (await harness.Published.Any<PositionReconciliationDrift>()).Should().BeTrue();

        (await harness.Published.Any<OrderApproved>()).Should().BeFalse();
        (await harness.Published.Any<PositionCloseRequested>()).Should().BeFalse();
    }
}
