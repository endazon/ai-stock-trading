using AiStockTrading.Configuration.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.Backtest.Domain.Tests;

// FR-15, 06_daytrading-review §3.2, IADR-0037: 決定的シミュレーション。ルックアヘッド排除
// （判断=当日終値まで／約定=翌営業日始値）とコスト計上・時価評価を検証する。
public class BacktestSimulatorTests
{
    private static readonly BacktestCostModel ZeroCost =
        new(TradingAssumptionsDefaults.Create(), SlippageRatio: 0m);

    private static PriceBar Bar(int day, decimal open, decimal close) =>
        new("AAA", Market.UnitedStates, new DateOnly(2024, 1, day), open, Math.Max(open, close), Math.Min(open, close), close, 1_000);

    // 指定日に指定注文を出すスクリプト戦略。
    private sealed class ScriptedStrategy(Dictionary<DateOnly, BacktestOrder[]> script) : IBacktestStrategy
    {
        public IReadOnlyList<BacktestOrder> DecideOrders(BacktestContext context) =>
            script.TryGetValue(context.AsOf, out var orders) ? orders : [];
    }

    // 各判断時に渡された履歴の最終日を記録する監視戦略（ルックアヘッド検証用）。
    private sealed class RecordingStrategy : IBacktestStrategy
    {
        public List<(DateOnly AsOf, DateOnly MaxHistoryDate)> Calls { get; } = [];

        public IReadOnlyList<BacktestOrder> DecideOrders(BacktestContext context)
        {
            Calls.Add((context.AsOf, context.History.Max(b => b.Date)));
            return [];
        }
    }

    [Fact]
    public void 判断には当日までの履歴しか渡さない_ルックアヘッド排除()
    {
        var bars = new[] { Bar(1, 10m, 10m), Bar(2, 10m, 10m), Bar(3, 10m, 10m) };
        var strategy = new RecordingStrategy();

        BacktestSimulator.Run(bars, strategy, new BacktestConfig(1_000m, ZeroCost, CostSensitivity.Baseline));

        // 各判断で履歴の最終日は判断日と一致し、未来のバーは含まれない。
        strategy.Calls.Should().OnlyContain(c => c.MaxHistoryDate == c.AsOf);
        strategy.Calls.Select(c => c.AsOf).Should().ContainInOrder(
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 3));
    }

    [Fact]
    public void 注文は判断の翌営業日始値で約定する()
    {
        // Day1 終値で 10 株買い → Day2 始値 12 で約定。
        var bars = new[] { Bar(1, 10m, 10m), Bar(2, 12m, 13m), Bar(3, 13m, 13m) };
        var script = new Dictionary<DateOnly, BacktestOrder[]>
        {
            [new DateOnly(2024, 1, 1)] = [new BacktestOrder("AAA", Market.UnitedStates, 10)],
        };

        var run = BacktestSimulator.Run(bars, new ScriptedStrategy(script),
            new BacktestConfig(1_000m, ZeroCost, CostSensitivity.Baseline));

        run.Fills.Should().ContainSingle();
        var fill = run.Fills[0];
        fill.Date.Should().Be(new DateOnly(2024, 1, 2));
        fill.Price.Should().Be(12m); // Day2 始値
        fill.SignedQuantity.Should().Be(10);
    }

    [Fact]
    public void エクイティ曲線は終値で時価評価される()
    {
        // 初期 1000。Day1 終値で 10 株買い → Day2 始値 10 で約定（現金 1000-100=900）。
        // Day2 終値 11 → エクイティ = 900 + 10*11 = 1010。
        var bars = new[] { Bar(1, 10m, 10m), Bar(2, 10m, 11m), Bar(3, 11m, 11m) };
        var script = new Dictionary<DateOnly, BacktestOrder[]>
        {
            [new DateOnly(2024, 1, 1)] = [new BacktestOrder("AAA", Market.UnitedStates, 10)],
        };

        var run = BacktestSimulator.Run(bars, new ScriptedStrategy(script),
            new BacktestConfig(1_000m, ZeroCost, CostSensitivity.Baseline));

        run.EquityCurve.Should().HaveCount(3);
        run.EquityCurve[0].Should().Be(1_000m); // Day1 終値: 建玉なし
        run.EquityCurve[1].Should().Be(1_010m); // Day2 終値: 現金900 + 10株*11
    }

    [Fact]
    public void 決済で実現損益が計上され費用が現金から差し引かれる()
    {
        // 手数料 1%・スリッページ 0 のコストモデル。
        var assumptions = TradingAssumptionsDefaults.Create() with
        {
            UnitedStatesCommission = new CommissionSchedule(0.01m, 0m, 0m),
        };
        var costModel = new BacktestCostModel(assumptions, 0m);

        // Day1: 10株買い → Day2 始値 10 で約定（notional 100、費用 1）。
        // Day2: 10株売り → Day3 始値 15 で約定（notional 150、費用 1.5）。実現 = (15-10)*10 = 50。
        var bars = new[] { Bar(1, 10m, 10m), Bar(2, 10m, 10m), Bar(3, 15m, 15m) };
        var script = new Dictionary<DateOnly, BacktestOrder[]>
        {
            [new DateOnly(2024, 1, 1)] = [new BacktestOrder("AAA", Market.UnitedStates, 10)],
            [new DateOnly(2024, 1, 2)] = [new BacktestOrder("AAA", Market.UnitedStates, -10)],
        };

        var run = BacktestSimulator.Run(bars, new ScriptedStrategy(script),
            new BacktestConfig(1_000m, costModel, CostSensitivity.Baseline));

        run.RealizedTradePnls.Should().ContainSingle().Which.Should().Be(50m);
        // 現金: 1000 −100(買い) −1(費用) +150(売り) −1.5(費用) = 1047.5。
        run.EquityCurve[^1].Should().Be(1_047.5m);
    }

    [Fact]
    public void バーが無い戦略はノーオペで初期資金を維持する()
    {
        var bars = new[] { Bar(1, 10m, 10m), Bar(2, 10m, 10m) };
        var run = BacktestSimulator.Run(bars, new ScriptedStrategy([]),
            new BacktestConfig(1_000m, ZeroCost, CostSensitivity.Baseline));

        run.EquityCurve.Should().OnlyContain(e => e == 1_000m);
        run.Metrics.TotalReturn.Should().Be(0m);
        run.Fills.Should().BeEmpty();
    }
}
