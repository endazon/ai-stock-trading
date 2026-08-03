using AiStockTrading.Configuration.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Backtest.Domain.Tests;

// FR-15, 06_daytrading-review §3.2, IADR-0043: 決定的シミュレーション。ルックアヘッド排除
// （判断=当日終値まで／約定=翌営業日始値）とコスト計上・時価評価を検証する。
public class BacktestSimulatorTests
{
    private static readonly BacktestCostModel ZeroCost =
        new(TradingAssumptionsDefaults.Create(), SlippageRatio: 0m);

    private static PriceBar Bar(int day, decimal open, decimal close) =>
        Bar("AAA", day, open, close);

    private static PriceBar Bar(string symbol, int day, decimal open, decimal close) =>
        new(symbol, Market.UnitedStates, new DateOnly(2024, 1, day), open, Math.Max(open, close), Math.Min(open, close), close, 1_000);

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
        run.UnfilledOrderCount.Should().Be(0);
    }

    [Fact]
    public void 翌営業日が無い最終日の注文は未約定として計上される()
    {
        // 最終日（Day2）に出した注文は翌営業日が無く約定しない。無音破棄せず件数を残す（レビュー指摘 #99）。
        var bars = new[] { Bar(1, 10m, 10m), Bar(2, 10m, 10m) };
        var script = new Dictionary<DateOnly, BacktestOrder[]>
        {
            [new DateOnly(2024, 1, 2)] = [new BacktestOrder("AAA", Market.UnitedStates, 10)],
        };

        var run = BacktestSimulator.Run(bars, new ScriptedStrategy(script),
            new BacktestConfig(1_000m, ZeroCost, CostSensitivity.Baseline));

        run.Fills.Should().BeEmpty();
        run.UnfilledOrderCount.Should().Be(1);
    }

    [Fact]
    public void 上場廃止で以降バーが来ない建玉は最終終値で凍結評価される_SliceA既知の制約()
    {
        // Slice A の既知制約（#99 レビュー指摘）: シミュレータは渡されたバーのみを見る純関数で「上場廃止」を知らない。
        // PIT ユニバース（SecurityUniverse／BacktestRunner）で廃止後バーが除外されると、当該建玉は以降マークが
        // 更新されず「最終観測終値」で凍結評価され続ける（強制決済・回収価値モデルは Slice B/C で扱う）。
        // 本テストはこの挙動を意図として固定し、将来の変更を明示的にする。
        // AAA を Day2 まで保有 → Day3 以降 AAA のバーは来ない（廃止相当）。BBB が timeline を延長する。
        var bars = new[]
        {
            Bar("AAA", 1, 10m, 10m), Bar("BBB", 1, 5m, 5m),
            Bar("AAA", 2, 10m, 20m), Bar("BBB", 2, 5m, 5m), // Day2: AAA 約定 @10、終値 20 を記録
            Bar("BBB", 3, 5m, 5m),                          // Day3: AAA バー無し（廃止相当）
            Bar("BBB", 4, 5m, 5m),                          // Day4: AAA バー無し
        };
        var script = new Dictionary<DateOnly, BacktestOrder[]>
        {
            [new DateOnly(2024, 1, 1)] = [new BacktestOrder("AAA", Market.UnitedStates, 10)],
        };

        var run = BacktestSimulator.Run(bars, new ScriptedStrategy(script),
            new BacktestConfig(1_000m, ZeroCost, CostSensitivity.Baseline));

        // Day2 に AAA 10株を始値 10 で約定（現金 1000-100=900）。以降 AAA のバーは来ず追加約定なし。
        run.Fills.Should().ContainSingle();
        run.UnfilledOrderCount.Should().Be(0); // AAA への新規注文は Day1 のみ → 未約定は発生しない
        // Day3・Day4 は AAA を最終終値 20 で凍結評価: 900 + 10*20 = 1100（値が更新されず一定）。
        run.EquityCurve.Should().HaveCount(4);
        run.EquityCurve[2].Should().Be(1_100m);
        run.EquityCurve[3].Should().Be(1_100m); // 廃止後も同一価格で凍結（Slice A の既知制約）
    }

    [Fact]
    public void 同一銘柄同日の重複バーは後勝ちで例外にしない()
    {
        // IBarDataSource（外部境界）が同一 (Symbol,Market,Date) を重複返却しても例外にせず、後勝ちで評価する（レビュー指摘 #99）。
        var bars = new[]
        {
            Bar(1, 10m, 10m),
            new PriceBar("AAA", Market.UnitedStates, new DateOnly(2024, 1, 1), 10m, 10m, 10m, 99m, 1_000), // 重複（後勝ち）
            Bar(2, 10m, 10m),
        };

        var act = () => BacktestSimulator.Run(bars, new ScriptedStrategy([]),
            new BacktestConfig(1_000m, ZeroCost, CostSensitivity.Baseline));

        act.Should().NotThrow();
    }
}
