using AiStockTrading.Configuration.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Backtest.Domain;

// FR-15, 06_daytrading-review §3.2: コストの感度分析。Baseline=現実的コスト、Doubled=コスト2倍。
// 「コストを2倍にしても期待値が正」を Stage 0 合格条件にするため、同一戦略を 1x/2x で走らせる。
public enum CostSensitivity
{
    Baseline,
    Doubled,
}

// FR-15, FR-17, IADR-0037: バックテストのコストモデル。FR-17 の概算費用関数（CostCalculator）を基礎に、
// バックテスト固有のスリッページ（約定代金比・片道）とコスト倍率（感度分析）を上乗せする薄いラッパ。
// 判断時見積り・事後集計・バックテストが同一の費用式を共有する（乖離防止）。
public sealed record BacktestCostModel(TradingAssumptions Assumptions, decimal SlippageRatio)
{
    // 感度倍率（Baseline=1・Doubled=2）。
    public static decimal Multiplier(CostSensitivity sensitivity) =>
        sensitivity == CostSensitivity.Doubled ? 2m : 1m;

    // 片道の費用 = (FR-17 概算費用 ＋ スリッページ) × 感度倍率。
    public decimal OneWayCost(Market market, decimal notional, CostSensitivity sensitivity)
    {
        var slippage = Math.Max(0m, notional) * SlippageRatio;
        var baseCost = CostCalculator.EstimateOneWayCost(Assumptions, market, notional) + slippage;
        return baseCost * Multiplier(sensitivity);
    }

    // 往復（建て＋手仕舞い）の費用 = 片道 × 2。
    public decimal RoundTripCost(Market market, decimal notional, CostSensitivity sensitivity) =>
        2m * OneWayCost(market, notional, sensitivity);
}
