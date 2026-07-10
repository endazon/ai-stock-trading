using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-10, FR-05, IADR-0018: 台帳ストア（承認→約定の相関・冪等）と、それを射影するプロバイダの検証。
public class LedgerPortfolioStateProviderTests
{
    private static OrderIntent BuyIntent(int qty, decimal price) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper, qty, price);

    [Fact]
    public void 承認を記録した約定は射影に反映される()
    {
        var store = new InMemoryPortfolioLedgerStore();
        var decisionId = Guid.NewGuid();
        store.AppendApproval(decisionId, BuyIntent(10, 1_000m), DateTimeOffset.UtcNow);
        var appended = store.AppendFill(decisionId, "ORD-1", 10, 1_000m, DateTimeOffset.UtcNow);

        appended.Should().BeTrue();

        var provider = new LedgerPortfolioStateProvider(store, new FixedClock());
        var state = provider.GetCurrent();

        state.OpenPositionCount.Should().Be(1);
        state.InvestedCapital.Should().Be(10_000m);
        state.Capital.Should().Be(TradingDefaults.InitialCapital);
    }

    [Fact]
    public void 承認のない約定は記録されず_false_を返す()
    {
        var store = new InMemoryPortfolioLedgerStore();
        var appended = store.AppendFill(Guid.NewGuid(), "ORD-X", 10, 1_000m, DateTimeOffset.UtcNow);

        appended.Should().BeFalse();
        store.GetFills().Should().BeEmpty();
    }

    [Fact]
    public void 同一_OrderId_の再送は重複記録しない()
    {
        var store = new InMemoryPortfolioLedgerStore();
        var decisionId = Guid.NewGuid();
        store.AppendApproval(decisionId, BuyIntent(10, 1_000m), DateTimeOffset.UtcNow);
        store.AppendFill(decisionId, "ORD-1", 10, 1_000m, DateTimeOffset.UtcNow);
        store.AppendFill(decisionId, "ORD-1", 10, 1_000m, DateTimeOffset.UtcNow); // 再送

        store.GetFills().Should().HaveCount(1);
    }

    [Fact]
    public void 同一_DecisionId_の承認再送は重複しない()
    {
        var store = new InMemoryPortfolioLedgerStore();
        var decisionId = Guid.NewGuid();
        store.AppendApproval(decisionId, BuyIntent(10, 1_000m), DateTimeOffset.UtcNow);
        store.AppendApproval(decisionId, BuyIntent(999, 9_999m), DateTimeOffset.UtcNow); // 再送（無視される）
        store.AppendFill(decisionId, "ORD-1", 10, 1_000m, DateTimeOffset.UtcNow);

        var fills = store.GetFills();
        fills.Should().HaveCount(1);
        fills[0].Quantity.Should().Be(10);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 7, 10, 3, 0, 0, TimeSpan.Zero);
        public DateOnly Today => new(2026, 7, 10);
    }
}
