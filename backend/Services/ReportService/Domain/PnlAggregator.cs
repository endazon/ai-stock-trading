using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace ReportService.Domain;

// FR-16, 04_report-templates 数値定義, IADR-0025: 損益集計の純関数。約定列を平均取得単価法で畳み込み、
// 前提条件（手数料/為替/税率）を用いてテンプレート定義どおりに実現損益・費用・税・評価損益を集計する（数値は LLM に計算させない）。
public static class PnlAggregator
{
    /// <param name="adoptions">
    /// FR-11, ADR-0041 決定 1, #870, #859, IADR-0360 決定 4: 期間の<b>乖離の取り込み</b>（システム外の売買）。
    /// 🔴 <b>数量だけを在庫へ反映する。</b> 実現損益・費用合計・約定件数・決済件数・勝ち決済件数のいずれにも算入しない
    /// ——システム外の売買は約定価格が分からず、実現損益は<b>不明</b>である（0 ではない）。
    /// 反映しないと、既に決済された建玉が「期間内に決済されなかった建玉」として残り、
    /// <b>実在しない建玉の評価損益</b>が <see cref="PnlSummary.UnrealizedPnl"/> に出る（#859 の主訴）。
    /// <c>null</c>＝取り込みを照会できていない／空列＝該当なし。
    /// </param>
    public static PnlSummary Aggregate(
        IReadOnlyList<PeriodTradeFill> fills,
        TradingAssumptions assumptions,
        IReadOnlyDictionary<string, decimal>? currentPrices = null,
        IReadOnlyList<PeriodDriftAdoption>? adoptions = null)
    {
        ArgumentNullException.ThrowIfNull(fills);
        ArgumentNullException.ThrowIfNull(assumptions);

        var positions = new Dictionary<(string Symbol, Market Market), (int Qty, decimal AvgCost)>();
        var realizedGross = 0m;
        var totalCost = 0m;
        var realizingCount = 0;
        var winningCount = 0;

        // #870, #859, IADR-0360 決定 4: 約定と取り込みを同じ時系列で畳む（順序の定義は PeriodLedgerTimeline が持つ）。
        foreach (var entry in PeriodLedgerTimeline.Merge(fills, adoptions))
        {
            // 🔴 取り込みは**在庫だけ**を動かす。費用・実現損益・決済件数・勝ち決済件数のどれにも触れない
            //（触れれば「分からない値」が確定値として §1 サマリへ入る）。
            if (entry.Adoption is { } adoption)
            {
                var adoptionKey = (adoption.Symbol, adoption.Market);
                positions.TryGetValue(adoptionKey, out var held);
                var reduced = adoption.ApplyTo(new InventoryLot(held.Qty, held.AvgCost));
                positions[adoptionKey] = (reduced.Quantity, reduced.AverageCost);
                continue;
            }

            var fill = entry.Fill!;

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
                if (applied.RealizedPnl > 0m)
                    winningCount++; // 勝ち決済（勝率の分子）
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

        return new PnlSummary(realizedGross, totalCost, tax, net, unrealized, fills.Count, realizingCount, winningCount);
    }
}
