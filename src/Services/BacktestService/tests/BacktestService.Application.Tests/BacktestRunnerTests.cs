using AiStockTrading.Backtest.Application;
using AiStockTrading.Backtest.Domain;
using AiStockTrading.Configuration.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.Backtest.Application.Tests;

// FR-15, IADR-0037: 過去データ供給の抽象（IBarDataSource）と PIT ユニバースでの生存者バイアス排除を検証する。
public class BacktestRunnerTests
{
    private static readonly BacktestCostModel ZeroCost =
        new(TradingAssumptionsDefaults.Create(), SlippageRatio: 0m);

    private static PriceBar Bar(string symbol, int day, decimal price) =>
        new(symbol, Market.UnitedStates, new DateOnly(2024, 1, day), price, price, price, price, 1_000);

    private sealed class NoopStrategy : IBacktestStrategy
    {
        public IReadOnlyList<BacktestOrder> DecideOrders(BacktestContext context) => [];
    }

    [Fact]
    public void データ源から期間内のバーを取得してシミュレーションする()
    {
        var data = new InMemoryBarDataSource([Bar("AAA", 1, 10m), Bar("AAA", 2, 11m), Bar("AAA", 3, 12m)]);
        var universe = new SecurityUniverse([new UniverseMembership("AAA", Market.UnitedStates, new DateOnly(2024, 1, 1), null)]);
        var runner = new BacktestRunner(data);

        var run = runner.Run(new BacktestRequest(universe, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 3),
            new NoopStrategy(), new BacktestConfig(1_000m, ZeroCost, CostSensitivity.Baseline)));

        run.EquityCurve.Should().HaveCount(3); // 3 営業日
    }

    [Fact]
    public void PITユニバースで上場廃止後のバーを除外する_生存者バイアス排除()
    {
        // BBB は Day2 に上場廃止（Day2 以降は非構成）。データ源には Day1〜3 のバーがある。
        var data = new InMemoryBarDataSource(
        [
            Bar("BBB", 1, 10m), Bar("BBB", 2, 10m), Bar("BBB", 3, 10m),
        ]);
        var universe = new SecurityUniverse(
        [
            new UniverseMembership("BBB", Market.UnitedStates, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 2)),
        ]);
        var runner = new BacktestRunner(data);

        // 記録戦略で、渡された履歴に Day2 以降の BBB が含まれないことを検証する。
        var recorder = new HistoryRecorder();
        runner.Run(new BacktestRequest(universe, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 3),
            recorder, new BacktestConfig(1_000m, ZeroCost, CostSensitivity.Baseline)));

        recorder.AllHistoryBars.Should().OnlyContain(b => b.Date == new DateOnly(2024, 1, 1));
    }

    [Fact]
    public void 期間外のバーは取得されない()
    {
        var data = new InMemoryBarDataSource([Bar("AAA", 1, 10m), Bar("AAA", 5, 10m), Bar("AAA", 9, 10m)]);
        data.GetBars(new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 6))
            .Should().ContainSingle().Which.Date.Should().Be(new DateOnly(2024, 1, 5));
    }

    private sealed class HistoryRecorder : IBacktestStrategy
    {
        public List<PriceBar> AllHistoryBars { get; } = [];

        public IReadOnlyList<BacktestOrder> DecideOrders(BacktestContext context)
        {
            AllHistoryBars.AddRange(context.History);
            return [];
        }
    }
}
