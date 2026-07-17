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

    // --- エクイティピーク（#81, IADR-0065）: 台帳から再計算する（永続化しない） ---

    private static LedgerFill Fill(TradeSide side, int qty, decimal price, int day) =>
        new("AAPL", Market.UnitedStates, side, PositionEffect.Open, qty, price,
            new DateTimeOffset(2026, 7, day, 0, 0, 0, TimeSpan.FromHours(9)));

    [Fact]
    public void エクイティピークは約定が無ければ初期資金と現在エクイティの最大()
    {
        PortfolioValuation.EquityHighWaterMark([], 100_000m, 100_000m).Should().Be(100_000m);
        PortfolioValuation.EquityHighWaterMark([], 100_000m, 120_000m).Should().Be(120_000m);
        // 現在エクイティが初期資金を下回っても、ピークは初期資金（＝開始時点が山）を下回らない。
        PortfolioValuation.EquityHighWaterMark([], 100_000m, 90_000m).Should().Be(100_000m);
    }

    [Fact]
    public void エクイティピークは実現損益の走査最大を採り下落後も保持する()
    {
        // 10 株を 1,000 で建て 1,200 で決済（+2,000 実現）→ ピーク 102,000。
        // その後 10 株を 1,000 で建て 900 で決済（−1,000 実現）→ 現在 101,000 だがピークは 102,000 のまま。
        LedgerFill[] fills =
        [
            Fill(TradeSide.Buy, 10, 1_000m, 1),
            Fill(TradeSide.Sell, 10, 1_200m, 2),
            Fill(TradeSide.Buy, 10, 1_000m, 3),
            Fill(TradeSide.Sell, 10, 900m, 4),
        ];

        PortfolioValuation.EquityHighWaterMark(fills, 100_000m, 101_000m).Should().Be(102_000m);
    }

    [Fact]
    public void エクイティピークは現在エクイティが過去の山を超えればそれを採る()
    {
        // 含み益で現在エクイティが過去の実現ベースの山（102,000）を超えるケース。
        LedgerFill[] fills = [Fill(TradeSide.Buy, 10, 1_000m, 1), Fill(TradeSide.Sell, 10, 1_200m, 2)];

        PortfolioValuation.EquityHighWaterMark(fills, 100_000m, 105_000m).Should().Be(105_000m);
    }

    [Fact]
    public void エクイティピークは約定列の順序に依存しない()
    {
        // 入力が時系列順でなくても、畳み込み前に整列するため同じピークを返す（射影と同一の規約）。
        LedgerFill[] ordered = [Fill(TradeSide.Buy, 10, 1_000m, 1), Fill(TradeSide.Sell, 10, 1_200m, 2)];
        LedgerFill[] shuffled = [ordered[1], ordered[0]];

        PortfolioValuation.EquityHighWaterMark(shuffled, 100_000m, 100_000m)
            .Should().Be(PortfolioValuation.EquityHighWaterMark(ordered, 100_000m, 100_000m));
    }
}
