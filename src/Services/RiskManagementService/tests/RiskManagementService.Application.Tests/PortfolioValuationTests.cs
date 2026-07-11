using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-10, IADR-0008/0036: 含み損益・ドローダウンの時価評価（純関数）を検証する。
public class PortfolioValuationTests
{
    private static OpenPosition Pos(TradeSide side, int qty, decimal avgCost, string symbol = "AAPL") =>
        new(symbol, Market.UnitedStates, side, qty, avgCost);

    private static Dictionary<(string, Market), decimal> Prices(params (string Symbol, decimal Price)[] px) =>
        px.ToDictionary(p => (p.Symbol, Market.UnitedStates), p => p.Price);

    [Fact]
    public void 含み損益はロングの評価差額を符号付きで合算する()
    {
        var positions = new[] { Pos(TradeSide.Buy, 10, 1_000m) };

        PortfolioValuation.UnrealizedPnl(positions, Prices(("AAPL", 1_100m))).Should().Be(1_000m);  // 含み益
        PortfolioValuation.UnrealizedPnl(positions, Prices(("AAPL", 900m))).Should().Be(-1_000m);   // 含み損
    }

    [Fact]
    public void 含み損益はショートで価格下落を益として算出する()
    {
        // ショート 5 @2,000 が 1,900 → (1,900−2,000)×(−5) = +500。
        var positions = new[] { Pos(TradeSide.Sell, 5, 2_000m) };

        PortfolioValuation.UnrealizedPnl(positions, Prices(("AAPL", 1_900m))).Should().Be(500m);
    }

    [Fact]
    public void 含み損益は現在値の無い建玉を0として扱う()
    {
        var positions = new[] { Pos(TradeSide.Buy, 10, 1_000m, "AAPL"), Pos(TradeSide.Buy, 5, 2_000m, "MSFT") };

        // AAPL のみ現在値あり（+1,000）。MSFT は欠損 → 0。
        PortfolioValuation.UnrealizedPnl(positions, Prices(("AAPL", 1_100m))).Should().Be(1_000m);
    }

    [Fact]
    public void 含み損益は現在値辞書が_null_なら0()
    {
        PortfolioValuation.UnrealizedPnl(new[] { Pos(TradeSide.Buy, 10, 1_000m) }, null).Should().Be(0m);
    }

    [Theory]
    [InlineData(110_000, 99_000, 0.10)]  // 10% 下落
    [InlineData(100_000, 100_000, 0.0)]  // 下落なし
    [InlineData(100_000, 105_000, 0.0)]  // 上昇（DD は 0 下限）
    public void ドローダウンはピークからの下落率を下限0で返す(int peak, int equity, double expected)
    {
        PortfolioValuation.DrawdownRatio(peak, equity).Should().Be((decimal)expected);
    }

    [Fact]
    public void ドローダウンはピーク未指定または非正なら0()
    {
        PortfolioValuation.DrawdownRatio(null, 90_000m).Should().Be(0m);
        PortfolioValuation.DrawdownRatio(0m, 90_000m).Should().Be(0m);
        PortfolioValuation.DrawdownRatio(-1m, 90_000m).Should().Be(0m);
    }
}
