using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
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
}
