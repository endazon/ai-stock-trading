using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-10, #292, IADR-0117: 処理中（承認済み・未約定）の決済数量。EfPortfolioLedgerStore と同一の意味論を
// インメモリ実装側で固定する（同名テストが Worker.Tests 側にもあり、両実装の乖離を検知する）。
public class PortfolioLedgerInFlightCloseTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Window = Now.AddMinutes(-30);

    private static Guid Approve(
        InMemoryPortfolioLedgerStore ledger,
        PositionEffect effect,
        int quantity,
        DateTimeOffset approvedAt,
        string symbol = "AAPL",
        Market market = Market.UnitedStates)
    {
        var decisionId = Guid.NewGuid();
        ledger.AppendApproval(
            decisionId,
            new OrderIntent(symbol, market, TradeSide.Sell, ProductType.Cash, TradeMode.Paper,
                quantity, 21m, effect, StopLossPrice: null, FxRateToBase: 1m),
            approvedAt);
        return decisionId;
    }

    [Fact]
    public void 承認も約定も無ければゼロ()
    {
        new InMemoryPortfolioLedgerStore()
            .GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 未約定の決済承認を数える()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        Approve(ledger, PositionEffect.Close, 60, Now.AddMinutes(-5));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(60);
    }

    [Fact]
    public void 複数の未約定決済を合計する()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        Approve(ledger, PositionEffect.Close, 60, Now.AddMinutes(-5));
        Approve(ledger, PositionEffect.Close, 15, Now.AddMinutes(-1));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(75);
    }

    [Fact]
    public void 部分約定は未約定ぶんだけを数える()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var id = Approve(ledger, PositionEffect.Close, 60, Now.AddMinutes(-5));
        ledger.AppendFill(id, "order-1", 20, 21m, Now.AddMinutes(-4));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(40);
    }

    [Fact]
    public void 全量約定した決済は数えない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var id = Approve(ledger, PositionEffect.Close, 60, Now.AddMinutes(-5));
        ledger.AppendFill(id, "order-1", 60, 21m, Now.AddMinutes(-4));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 約定が承認数量を超えても負にはしない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var id = Approve(ledger, PositionEffect.Close, 60, Now.AddMinutes(-5));
        ledger.AppendFill(id, "order-1", 70, 21m, Now.AddMinutes(-4));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 新規建ての承認は数えない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        Approve(ledger, PositionEffect.Open, 60, Now.AddMinutes(-5));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 窓より前に承認された決済は数えない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        Approve(ledger, PositionEffect.Close, 60, Now.AddMinutes(-31));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 別銘柄と別市場は数えない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        Approve(ledger, PositionEffect.Close, 60, Now.AddMinutes(-5), symbol: "MSFT");
        Approve(ledger, PositionEffect.Close, 30, Now.AddMinutes(-5), market: Market.Japan);

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }
}
