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

            var (realized, reduced) = Apply(ref pos, signedQ, fill.Price);
            positions[key] = pos;

            if (reduced)
            {
                realizedGross += realized;
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

    // 符号付き在庫へ 1 約定を適用する。戻り値: 減少分の実現損益（税引前）と、在庫が減少したか（決済か）。
    private static (decimal Realized, bool Reduced) Apply(ref (int Qty, decimal AvgCost) pos, int signedQ, decimal price)
    {
        if (pos.Qty == 0)
        {
            pos = (signedQ, price);
            return (0m, false);
        }

        if (Math.Sign(pos.Qty) == Math.Sign(signedQ))
        {
            // 建て増し（加重平均・実現なし）。
            var newQty = pos.Qty + signedQ;
            var total = pos.AvgCost * Math.Abs(pos.Qty) + price * Math.Abs(signedQ);
            pos = (newQty, total / Math.Abs(newQty));
            return (0m, false);
        }

        // 反対方向 = 決済（および反転の可能性）。減少分に実現損益を計上。
        var reduce = Math.Min(Math.Abs(pos.Qty), Math.Abs(signedQ));
        var realized = pos.Qty > 0 ? (price - pos.AvgCost) * reduce : (pos.AvgCost - price) * reduce;

        var resultQty = pos.Qty + signedQ;
        if (resultQty == 0)
            pos = (0, 0m);
        else if (Math.Sign(resultQty) != Math.Sign(pos.Qty))
            pos = (resultQty, price); // 反転
        else
            pos = (resultQty, pos.AvgCost); // 一部決済

        return (realized, true);
    }
}
