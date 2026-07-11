using AiStockTrading.Configuration.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Report.Domain;

// FR-16, 04_report-templates 数値定義, IADR-0025: 損益集計の純関数。約定列を平均取得単価法で畳み込み、
// 前提条件（手数料/為替/税率）を用いてテンプレート定義どおりに実現損益・費用・税・評価損益を集計する（数値は LLM に計算させない）。
public static class PnlAggregator
{
    public static PnlSummary Aggregate(
        IReadOnlyList<PeriodTradeFill> fills,
        TradingAssumptions assumptions,
        IReadOnlyDictionary<string, decimal>? currentPrices = null)
    {
        ArgumentNullException.ThrowIfNull(fills);
        ArgumentNullException.ThrowIfNull(assumptions);

        var positions = new Dictionary<(string Symbol, Market Market), (int Qty, decimal AvgCost)>();
        var realizedGross = 0m;
        var totalCost = 0m;
        var realizingCount = 0;

        foreach (var fill in fills.OrderBy(f => f.ExecutedAt))
        {
            // 費用合計 = Σ 概算費用（手数料＋為替スプレッド・#19 CostCalculator）。全約定に対して計上する。
            totalCost += CostCalculator.EstimateOneWayCost(assumptions, fill.Market, fill.Quantity * fill.Price);

            var key = (fill.Symbol, fill.Market);
            var signedQ = fill.Side == TradeSide.Buy ? fill.Quantity : -fill.Quantity;
            positions.TryGetValue(key, out var pos);

            // IADR-0033: 平均取得単価法の畳み込みは共有の純関数（SignedInventory）を単一情報源とする。
            var applied = SignedInventory.Apply(new InventoryLot(pos.Qty, pos.AvgCost), signedQ, fill.Price);
            positions[key] = (applied.Lot.Quantity, applied.Lot.AverageCost);

            if (applied.Reduced)
            {
                realizedGross += applied.RealizedPnl;
                realizingCount++;
            }
        }

        // 評価損益（税引前・参考）＝Σ 建玉 (現在値 − 平均取得単価)×数量（符号付き）。現在値の無い建玉は 0。
        var unrealized = 0m;
        foreach (var (key, pos) in positions)
        {
            if (pos.Qty == 0)
                continue;
            if (currentPrices is not null && currentPrices.TryGetValue(key.Symbol, out var price))
                unrealized += (price - pos.AvgCost) * pos.Qty;
        }

        // 源泉徴収税額＝利益にのみ課税（max(0, 実現損益(税引前) − 費用合計) × 譲渡益税率）。
        var taxableGain = realizedGross - totalCost;
        var tax = taxableGain > 0m ? taxableGain * assumptions.CapitalGainsTaxRate : 0m;
        var net = realizedGross - totalCost - tax;

        return new PnlSummary(realizedGross, totalCost, tax, net, unrealized, fills.Count, realizingCount);
    }
}
