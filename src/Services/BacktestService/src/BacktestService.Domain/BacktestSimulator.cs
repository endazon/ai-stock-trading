using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Backtest.Domain;

// FR-15, IADR-0037: 日足 OHLCV バー（不変レコード）。
public readonly record struct PriceBar(
    string Symbol,
    Market Market,
    DateOnly Date,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume);

// FR-15: 戦略が出す目標注文（符号付き数量。+ 買い / − 売り）。
public readonly record struct BacktestOrder(string Symbol, Market Market, int SignedQuantity);

// FR-15: 判断時のコンテキスト。History は当日 AsOf までのバーのみ（ルックアヘッド排除は構造で担保）。
public sealed record BacktestContext(
    DateOnly AsOf,
    IReadOnlyList<PriceBar> History,
    IReadOnlyDictionary<(string Symbol, Market Market), InventoryLot> Positions,
    decimal Cash);

// FR-15: バックテスト戦略。当日終値までの情報で目標注文を返す純関数。約定は翌営業日始値。
public interface IBacktestStrategy
{
    IReadOnlyList<BacktestOrder> DecideOrders(BacktestContext context);
}

// FR-15: 約定記録。Cost は片道費用、RealizedPnl は決済で確定した実現損益（グロス）。
public readonly record struct BacktestFill(
    DateOnly Date,
    string Symbol,
    Market Market,
    int SignedQuantity,
    decimal Price,
    decimal Cost,
    decimal RealizedPnl);

// FR-15: シミュレーション設定。
public sealed record BacktestConfig(decimal InitialCapital, BacktestCostModel CostModel, CostSensitivity Sensitivity);

// FR-15: シミュレーション結果。
public sealed record BacktestRun(
    IReadOnlyList<decimal> EquityCurve,
    IReadOnlyList<decimal> RealizedTradePnls,
    IReadOnlyList<BacktestFill> Fills,
    BacktestMetrics Metrics);

// FR-15, 06_daytrading-review §3.2, IADR-0037: 決定的バックテストシミュレータ（純関数）。
public static class BacktestSimulator
{
    public static BacktestRun Run(IReadOnlyList<PriceBar> bars, IBacktestStrategy strategy, BacktestConfig config)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(config);

        // 日付昇順に並べ、当日の (symbol,market) → バーで参照できるようにする。
        var ordered = bars.OrderBy(b => b.Date).ToList();
        var tradingDays = ordered.Select(b => b.Date).Distinct().OrderBy(d => d).ToList();
        var barsByDay = ordered
            .GroupBy(b => b.Date)
            .ToDictionary(g => g.Key, g => g.ToDictionary(b => (b.Symbol, b.Market)));

        var positions = new Dictionary<(string Symbol, Market Market), InventoryLot>();
        var lastClose = new Dictionary<(string Symbol, Market Market), decimal>();
        var cash = config.InitialCapital;

        // 前日終値で決めた注文を当日始値で約定させる（ルックアヘッド排除）。
        IReadOnlyList<BacktestOrder> pending = [];
        var history = new List<PriceBar>();

        var equityCurve = new List<decimal>(tradingDays.Count);
        var realizedTradePnls = new List<decimal>();
        var fills = new List<BacktestFill>();

        foreach (var tradingDay in tradingDays)
        {
            var todaysBars = barsByDay[tradingDay];

            // 1) 前日決定の注文を当日始値で約定。
            foreach (var order in pending)
            {
                var key = (order.Symbol, order.Market);
                if (order.SignedQuantity == 0 || !todaysBars.TryGetValue(key, out var bar))
                    continue; // 当日バーが無い銘柄は約定できない（次バーが無い）。

                var price = bar.Open;
                var notional = Math.Abs(order.SignedQuantity) * price;
                var cost = config.CostModel.OneWayCost(order.Market, notional, config.Sensitivity);

                var current = positions.TryGetValue(key, out var lot) ? lot : new InventoryLot(0, 0m);
                var result = SignedInventory.Apply(current, order.SignedQuantity, price);
                positions[key] = result.Lot;

                // 現金: 買い(+数量)は減少、売り(−数量)は増加。費用は常に現金から差し引く。
                cash -= order.SignedQuantity * price;
                cash -= cost;

                if (result.Reduced)
                    realizedTradePnls.Add(result.RealizedPnl);

                fills.Add(new BacktestFill(tradingDay, order.Symbol, order.Market,
                    order.SignedQuantity, price, cost, result.Reduced ? result.RealizedPnl : 0m));
            }

            // 2) 当日終値を記録し、履歴へ追加。
            foreach (var (key, bar) in todaysBars)
                lastClose[key] = bar.Close;
            history.AddRange(todaysBars.Values.OrderBy(b => b.Symbol));

            // 3) 当日終値の情報で戦略が翌日の注文を決定（履歴は当日まで）。
            var context = new BacktestContext(tradingDay, history, positions, cash);
            pending = strategy.DecideOrders(context) ?? [];

            // 4) 当日終値で時価評価しエクイティ曲線へ。
            var holdings = 0m;
            foreach (var (key, lot) in positions)
            {
                if (lot.Quantity == 0)
                    continue;
                var mark = lastClose.TryGetValue(key, out var close) ? close : lot.AverageCost;
                holdings += lot.Quantity * mark;
            }
            equityCurve.Add(cash + holdings);
        }

        var metrics = BacktestMetricsCalculator.Compute(equityCurve, realizedTradePnls);
        return new BacktestRun(equityCurve, realizedTradePnls, fills, metrics);
    }
}
