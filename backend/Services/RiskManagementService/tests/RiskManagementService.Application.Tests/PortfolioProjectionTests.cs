using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-10, FR-05, IADR-0018: 取引台帳から PortfolioState を組み立てる純射影の検証。
public class PortfolioProjectionTests
{
    private const decimal InitialCapital = 100_000m;
    private static readonly DateOnly Today = new(2026, 7, 10);

    // JST 当日の約定時刻（+9 の 12:00 = UTC 03:00）。
    private static DateTimeOffset TodayAt(int hour = 12) =>
        new(2026, 7, 10, hour, 0, 0, TimeSpan.FromHours(9));

    private static DateTimeOffset OnDay(int day, int hour = 12) =>
        new(2026, 7, day, hour, 0, 0, TimeSpan.FromHours(9));

    private static LedgerFill Fill(
        TradeSide side, PositionEffect effect, int qty, decimal price,
        DateTimeOffset at, string symbol = "AAPL", Market market = Market.UnitedStates) =>
        new(symbol, market, side, effect, qty, price, at);

    [Fact]
    public void 買い建ては建玉_取得額_保有数_当日取引銘柄に反映される()
    {
        var state = PortfolioProjection.Project(
            new[] { Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt()) },
            Today, InitialCapital);

        state.OpenPositionCount.Should().Be(1);
        state.InvestedCapital.Should().Be(10_000m);
        state.DailyOrderedAmount.Should().Be(10_000m);
        state.SymbolsTradedToday.Should().Contain(("AAPL", Market.UnitedStates));
        state.DailyRealizedPnl.Should().Be(0m);
        state.Capital.Should().Be(InitialCapital);
    }

    [Fact]
    public void 一部決済は平均取得単価で実現損益を計上し残建玉が残る()
    {
        // 10 株 @1,000 で建て、6 株を @1,200 で決済 → 実現 (1,200-1,000)*6 = +1,200。残 4 株 @1,000。
        var state = PortfolioProjection.Project(
            new[]
            {
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt(9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 6, 1_200m, TodayAt(10)),
            },
            Today, InitialCapital);

        state.DailyRealizedPnl.Should().Be(1_200m);
        state.OpenPositionCount.Should().Be(1);
        state.InvestedCapital.Should().Be(4_000m);
    }

    [Fact]
    public void 全決済で建玉はゼロになり保有数と取得額がゼロになる()
    {
        var state = PortfolioProjection.Project(
            new[]
            {
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt(9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 10, 900m, TodayAt(10)),
            },
            Today, InitialCapital);

        state.OpenPositionCount.Should().Be(0);
        state.InvestedCapital.Should().Be(0m);
        state.DailyRealizedPnl.Should().Be(-1_000m); // (900-1000)*10
    }

    [Fact]
    public void ショート建ては値下がりで利益になる()
    {
        // Sell 建て 10 @1,000、Buy で決済 @800 → 実現 (1,000-800)*10 = +2,000。
        var state = PortfolioProjection.Project(
            new[]
            {
                Fill(TradeSide.Sell, PositionEffect.Open, 10, 1_000m, TodayAt(9)),
                Fill(TradeSide.Buy, PositionEffect.Close, 10, 800m, TodayAt(10)),
            },
            Today, InitialCapital);

        state.DailyRealizedPnl.Should().Be(2_000m);
        state.OpenPositionCount.Should().Be(0);
    }

    [Fact]
    public void 資金は当日より前の実現損益を反映し当日実現は含めない()
    {
        // 前日: +3,000 の実現。当日: -500 の実現。当日開始資金 = 100,000 + 3,000。当日実現は Capital に含めない。
        var state = PortfolioProjection.Project(
            new[]
            {
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, OnDay(9, 9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 10, 1_300m, OnDay(9, 10)), // 前日 +3,000
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt(9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 10, 950m, TodayAt(10)),    // 当日 -500
            },
            Today, InitialCapital);

        state.Capital.Should().Be(103_000m);
        state.DailyRealizedPnl.Should().Be(-500m);
    }

    [Fact]
    public void 連敗は連続する損失決済を数え利益決済でリセットする()
    {
        // 損, 損, 益, 損 の順 → 直近から遡って連続損失は 1。
        var state = PortfolioProjection.Project(
            new[]
            {
                // 損 (-100)
                Fill(TradeSide.Buy, PositionEffect.Open, 1, 1_000m, OnDay(6, 9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 1, 900m, OnDay(6, 10)),
                // 損 (-100)
                Fill(TradeSide.Buy, PositionEffect.Open, 1, 1_000m, OnDay(7, 9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 1, 900m, OnDay(7, 10)),
                // 益 (+100) → リセット
                Fill(TradeSide.Buy, PositionEffect.Open, 1, 1_000m, OnDay(8, 9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 1, 1_100m, OnDay(8, 10)),
                // 損 (-100)
                Fill(TradeSide.Buy, PositionEffect.Open, 1, 1_000m, OnDay(9, 9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 1, 900m, OnDay(9, 10)),
            },
            Today, InitialCapital);

        state.ConsecutiveLosses.Should().Be(1);
    }

    [Fact]
    public void 連敗は複数の連続損失を積み上げる()
    {
        var state = PortfolioProjection.Project(
            new[]
            {
                Fill(TradeSide.Buy, PositionEffect.Open, 1, 1_000m, OnDay(7, 9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 1, 900m, OnDay(7, 10)),
                Fill(TradeSide.Buy, PositionEffect.Open, 1, 1_000m, OnDay(8, 9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 1, 800m, OnDay(8, 10)),
                Fill(TradeSide.Buy, PositionEffect.Open, 1, 1_000m, OnDay(9, 9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 1, 850m, OnDay(9, 10)),
            },
            Today, InitialCapital);

        state.ConsecutiveLosses.Should().Be(3);
    }

    [Fact]
    public void 当日発注金額は当日約定代金の合計になる()
    {
        var state = PortfolioProjection.Project(
            new[]
            {
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt(9)),   // 10,000
                Fill(TradeSide.Buy, PositionEffect.Open, 5, 2_000m, TodayAt(10), symbol: "MSFT"), // 10,000
                Fill(TradeSide.Buy, PositionEffect.Open, 3, 1_000m, OnDay(9, 10), symbol: "GOOG"), // 前日 → 発注額に含めない
            },
            Today, InitialCapital);

        state.DailyOrderedAmount.Should().Be(20_000m);
        state.OpenPositionCount.Should().Be(3); // AAPL, MSFT, GOOG
    }

    [Fact]
    public void 空の台帳は初期資金のみの状態を返す()
    {
        var state = PortfolioProjection.Project(Array.Empty<LedgerFill>(), Today, InitialCapital);

        state.Capital.Should().Be(InitialCapital);
        state.OpenPositionCount.Should().Be(0);
        state.InvestedCapital.Should().Be(0m);
        state.DailyRealizedPnl.Should().Be(0m);
        state.DailyOrderedAmount.Should().Be(0m);
        state.UnrealizedPnl.Should().Be(0m);
        state.DrawdownRatio.Should().Be(0m);
        state.ConsecutiveLosses.Should().Be(0);
        state.SymbolsTradedToday.Should().BeEmpty();
    }

    [Fact]
    public void 建て増しは加重平均で取得単価を更新する()
    {
        // 10 @1,000 と 10 @1,400 → 平均 1,200。20 株保有、取得額 24,000。
        var state = PortfolioProjection.Project(
            new[]
            {
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt(9)),
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_400m, TodayAt(10)),
            },
            Today, InitialCapital);

        state.InvestedCapital.Should().Be(24_000m);
        state.OpenPositionCount.Should().Be(1);
        state.DailyRealizedPnl.Should().Be(0m);
    }

    // FR-03, FR-10, IADR-0030: 保有ポジションの射影（損切りライン検知への供給）。
    [Fact]
    public void 保有射影は銘柄別ネット建玉を平均取得単価で返す()
    {
        var goog = "GOOG";
        var positions = PortfolioProjection.ProjectOpenPositions(
            new[]
            {
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt(9)),
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_400m, TodayAt(10)), // AAPL 平均 1,200・20株
                Fill(TradeSide.Buy, PositionEffect.Open, 5, 2_000m, TodayAt(11), symbol: goog),
            });

        positions.Should().HaveCount(2);
        var aapl = positions.Single(p => p.Symbol == "AAPL");
        aapl.Side.Should().Be(TradeSide.Buy);
        aapl.Quantity.Should().Be(20);
        aapl.AverageEntryPrice.Should().Be(1_200m);
        positions.Single(p => p.Symbol == goog).Quantity.Should().Be(5);
    }

    [Fact]
    public void 保有射影は全決済済み銘柄を除外する()
    {
        var positions = PortfolioProjection.ProjectOpenPositions(
            new[]
            {
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt(9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 10, 1_100m, TodayAt(10)), // 全決済
            });

        positions.Should().BeEmpty();
    }

    // FR-03/04/10, IADR-0035: 損切り価格（最新の同方向エントリー・一部決済で保持・反転で更新・欠損は null）。
    private static LedgerFill FillSl(TradeSide side, int qty, decimal price, decimal? stop, int hour, string symbol = "AAPL") =>
        new(symbol, Market.UnitedStates, side, side == TradeSide.Buy ? PositionEffect.Open : PositionEffect.Close,
            qty, price, TodayAt(hour), stop);

    [Fact]
    public void 保有射影の損切りは最新の建て増しエントリーを採る()
    {
        var positions = PortfolioProjection.ProjectOpenPositions(
            new[]
            {
                FillSl(TradeSide.Buy, 10, 1_000m, 970m, 9),
                FillSl(TradeSide.Buy, 10, 1_400m, 1_358m, 10), // 建て増し → 最新の損切りに更新
            });

        positions.Single().StopLossPrice.Should().Be(1_358m);
    }

    [Fact]
    public void 保有射影の損切りは一部決済で保持される()
    {
        var positions = PortfolioProjection.ProjectOpenPositions(
            new[]
            {
                FillSl(TradeSide.Buy, 10, 1_000m, 970m, 9),
                FillSl(TradeSide.Sell, 4, 1_100m, null, 10), // 一部決済（反対方向）→ 損切りは保持
            });

        positions.Single().Quantity.Should().Be(6);
        positions.Single().StopLossPrice.Should().Be(970m);
    }

    [Fact]
    public void 保有射影の損切りは反転で反転約定のエントリーに更新される()
    {
        var positions = PortfolioProjection.ProjectOpenPositions(
            new[]
            {
                FillSl(TradeSide.Buy, 5, 1_000m, 970m, 9),
                FillSl(TradeSide.Sell, 8, 1_100m, 1_133m, 10), // 反転 → 新ショートの損切り
            });

        var p = positions.Single();
        p.Side.Should().Be(TradeSide.Sell);
        p.StopLossPrice.Should().Be(1_133m);
    }

    [Fact]
    public void 保有射影の損切りは欠損なら_null()
    {
        var positions = PortfolioProjection.ProjectOpenPositions(
            new[] { FillSl(TradeSide.Buy, 10, 1_000m, null, 9) });

        positions.Single().StopLossPrice.Should().BeNull();
    }

    // FR-10, IADR-0036: 現在値・ピーク入力による含み損益・DD の時価算出。
    [Fact]
    public void 現在値とピークを与えると含み損益とDDを時価で算出する()
    {
        // AAPL 10 @1,000 を当日建て。現在値 1,100 → 含み +1,000。Capital=100,000・DailyRealizedPnl=0。
        // 現在エクイティ = 100,000 + 0 + 1,000 = 101,000。ピーク 105,000 → DD = (105,000−101,000)/105,000。
        var state = PortfolioProjection.Project(
            new[] { Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt(9)) },
            Today, InitialCapital,
            currentPrices: new Dictionary<(string, Market), decimal> { [("AAPL", Market.UnitedStates)] = 1_100m },
            equityHighWaterMark: 105_000m);

        state.UnrealizedPnl.Should().Be(1_000m);
        state.DrawdownRatio.Should().Be((105_000m - 101_000m) / 105_000m);
    }

    [Fact]
    public void 現在値未指定なら含み損益とDDは0のまま_回帰()
    {
        // IADR-0036: 既定（引数省略）は production 現挙動を保持（含み 0・DD 0）。
        var state = PortfolioProjection.Project(
            new[] { Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt(9)) },
            Today, InitialCapital);

        state.UnrealizedPnl.Should().Be(0m);
        state.DrawdownRatio.Should().Be(0m);
    }

    // --- FR-10, FR-17, #257, IADR-0107: 金額集計は基準通貨（円）、建玉はローカル通貨 ---

    private static LedgerFill UsdFill(
        TradeSide side, PositionEffect effect, int qty, decimal price, DateTimeOffset at, decimal rate) =>
        new("AAPL", Market.UnitedStates, side, effect, qty, price, at, StopLossPrice: null, FxRateToBase: rate);

    [Fact]
    public void 外貨建ての取得額と当日発注累計は基準通貨で積まれる()
    {
        // 20 USD × 10 株 × 150 円 = 30,000 円。換算しなければ 200（円と誤読）で統制が桁で緩む。
        var state = PortfolioProjection.Project(
            new[] { UsdFill(TradeSide.Buy, PositionEffect.Open, 10, 20m, TodayAt(), rate: 150m) },
            Today, InitialCapital);

        state.InvestedCapital.Should().Be(30_000m);
        state.DailyOrderedAmount.Should().Be(30_000m);
    }

    [Fact]
    public void 外貨建ての実現損益は基準通貨で計上される()
    {
        // 20 USD で 10 株建て（レート 150）→ 25 USD で全決済（レート 150）。実現＝5 USD × 10 × 150 = 7,500 円。
        var state = PortfolioProjection.Project(
            new[]
            {
                UsdFill(TradeSide.Buy, PositionEffect.Open, 10, 20m, TodayAt(9), rate: 150m),
                UsdFill(TradeSide.Sell, PositionEffect.Close, 10, 25m, TodayAt(11), rate: 150m),
            },
            Today, InitialCapital);

        state.DailyRealizedPnl.Should().Be(7_500m);
        state.OpenPositionCount.Should().Be(0);
    }

    [Fact]
    public void 外貨建ての含み損益は建玉の約定時レートで換算される()
    {
        // 20 USD → 現在値 25 USD。含み＝5 USD × 10 株 × 150 = 7,500 円。
        var prices = new Dictionary<(string, Market), decimal> { [("AAPL", Market.UnitedStates)] = 25m };

        var state = PortfolioProjection.Project(
            new[] { UsdFill(TradeSide.Buy, PositionEffect.Open, 10, 20m, TodayAt(), rate: 150m) },
            Today, InitialCapital, prices);

        state.UnrealizedPnl.Should().Be(7_500m);
    }

    [Fact]
    public void 建玉の平均取得単価はローカル通貨のまま射影される()
    {
        // 損切り検知（市場監視）は現在値（ローカル通貨）と比較するため、建玉側は換算しない。
        var positions = PortfolioProjection.ProjectOpenPositions(
            new[] { UsdFill(TradeSide.Buy, PositionEffect.Open, 10, 20m, TodayAt(), rate: 150m) });

        positions.Should().ContainSingle();
        positions[0].AverageEntryPrice.Should().Be(20m);
    }

    [Fact]
    public void レート未記録の約定は従来どおり基準通貨建てとして積まれる_回帰()
    {
        // 既定 1＝日本株および列追加前の既存データ。現行挙動と等価であることを固定する。
        var state = PortfolioProjection.Project(
            new[] { Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt(), symbol: "7203", market: Market.Japan) },
            Today, InitialCapital);

        state.InvestedCapital.Should().Be(10_000m);
        state.DailyOrderedAmount.Should().Be(10_000m);
    }
}
