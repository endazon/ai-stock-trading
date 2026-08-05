using AiStockTrading.Configuration.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Report.Domain.Tests;

// FR-16, 04_report-templates 数値定義, IADR-0025: 損益集計（実現損益・費用・税・評価損益）を検証する。
public class PnlAggregatorTests
{
    private const decimal TaxRate = 0.20315m;

    private static TradingAssumptions Assumptions(CommissionSchedule? commission = null, decimal fx = 0m) => new()
    {
        CapitalGainsTaxRate = TaxRate,
        JapanCommission = commission ?? new CommissionSchedule(0m, 0m, 0m),
        UnitedStatesCommission = commission ?? new CommissionSchedule(0m, 0m, 0m),
        FxSpreadRatio = fx,
        MinimumExpectedProfitMultiple = 1.5m,
        CostLimits = new MonthlyCostLimits(20_000m, 15_000m, 5_000m, 0m),
    };

    private static PeriodTradeFill Fill(
        TradeSide side, PositionEffect effect, int qty, decimal price, int minute,
        string sym = "AAPL", Market market = Market.UnitedStates) =>
        new(sym, market, side, effect, qty, price, new DateTimeOffset(2026, 7, 10, 0, minute, 0, TimeSpan.Zero));

    [Fact]
    public void 利益決済は実現損益と源泉徴収税と税引後を定義どおり算出する()
    {
        var fills = new[]
        {
            Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, 0),
            Fill(TradeSide.Sell, PositionEffect.Close, 10, 1_200m, 1),
        };

        var s = PnlAggregator.Aggregate(fills, Assumptions());

        s.RealizedPnlGross.Should().Be(2_000m);      // (1200-1000)*10
        s.TotalCost.Should().Be(0m);                 // 手数料/為替 未登録
        s.TaxWithheld.Should().Be(2_000m * TaxRate); // 利益に課税
        s.RealizedPnlNet.Should().Be(2_000m - 2_000m * TaxRate);
        s.RealizingTradeCount.Should().Be(1);
        s.TradeCount.Should().Be(2);
    }

    [Fact]
    public void 損失決済は源泉徴収税ゼロで税引後は税引前マイナス費用()
    {
        var fills = new[]
        {
            Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, 0),
            Fill(TradeSide.Sell, PositionEffect.Close, 10, 900m, 1),
        };

        var s = PnlAggregator.Aggregate(fills, Assumptions());

        s.RealizedPnlGross.Should().Be(-1_000m);
        s.TaxWithheld.Should().Be(0m);           // 損失には課税しない
        s.RealizedPnlNet.Should().Be(-1_000m);   // 費用0
    }

    [Fact]
    public void 費用合計は手数料と為替スプレッドを含む()
    {
        // 手数料 0.1%、為替スプレッド 0.2%。
        // #364, IADR-0152 決定7: 為替スプレッドは**非基準通貨市場**（基準通貨 USD では日本市場）に掛かる。
        var a = Assumptions(commission: new CommissionSchedule(0.001m, 0m, 0m), fx: 0.002m);
        var fills = new[]
        {
            // notional 10,000 → 手数料10 + 為替20 = 30
            Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, 0, sym: "7203", market: Market.Japan),
            // notional 12,000 → 手数料12 + 為替24 = 36
            Fill(TradeSide.Sell, PositionEffect.Close, 10, 1_200m, 1, sym: "7203", market: Market.Japan),
        };

        var s = PnlAggregator.Aggregate(fills, a);

        s.TotalCost.Should().Be(66m);
        s.RealizedPnlGross.Should().Be(2_000m);
        s.TaxWithheld.Should().Be((2_000m - 66m) * TaxRate);
        s.RealizedPnlNet.Should().Be(2_000m - 66m - (2_000m - 66m) * TaxRate);
    }

    [Fact]
    public void 評価損益は現在値から算出し現在値の無い建玉はゼロ()
    {
        var fills = new[] { Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, 0) }; // 建玉のみ（未決済）
        var prices = new Dictionary<string, decimal> { ["AAPL"] = 1_100m };

        var s = PnlAggregator.Aggregate(fills, Assumptions(), prices);

        s.UnrealizedPnl.Should().Be(1_000m); // (1100-1000)*10
        s.RealizedPnlGross.Should().Be(0m);

        // 現在値なしなら 0。
        PnlAggregator.Aggregate(fills, Assumptions()).UnrealizedPnl.Should().Be(0m);
    }

    [Fact]
    public void ショートは値下がりで利益になる()
    {
        var fills = new[]
        {
            Fill(TradeSide.Sell, PositionEffect.Open, 10, 1_000m, 0),
            Fill(TradeSide.Buy, PositionEffect.Close, 10, 800m, 1),
        };

        PnlAggregator.Aggregate(fills, Assumptions()).RealizedPnlGross.Should().Be(2_000m);
    }

    [Fact]
    public void 反転_ロングからショート_は決済分の実現損益と残ショートの評価損益を扱う()
    {
        // ロング10株保有中に15株売却 → 10株を決済（実現 +2,000）、残5株はショートへ転換（取得単価=1,200）。
        var fills = new[]
        {
            Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, 0),
            Fill(TradeSide.Sell, PositionEffect.Close, 15, 1_200m, 1),
        };
        var prices = new Dictionary<string, decimal> { ["AAPL"] = 1_100m };

        var s = PnlAggregator.Aggregate(fills, Assumptions(), prices);

        s.RealizedPnlGross.Should().Be(2_000m);       // (1200-1000)*10
        s.RealizingTradeCount.Should().Be(1);
        s.UnrealizedPnl.Should().Be(500m);            // ショート5 @1200・現在値1100 → (1100-1200)*(-5)=500
    }

    [Fact]
    public void 空の約定列はゼロサマリを返す()
    {
        var s = PnlAggregator.Aggregate(Array.Empty<PeriodTradeFill>(), Assumptions());
        s.RealizedPnlGross.Should().Be(0m);
        s.TotalCost.Should().Be(0m);
        s.TaxWithheld.Should().Be(0m);
        s.TradeCount.Should().Be(0);
    }
}
