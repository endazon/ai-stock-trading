using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace ReportService.Domain;

// FR-16, 04_report-templates 日報 §2, #563, IADR-0268: 台帳の約定列と**記録済みの判断根拠**から
// 日報 §2 の入力（TradeHistoryView）を組み立てる純関数。
//
// 🔴 **数値はコード集計値・文章は記録の転記であり、いずれも LLM に作らせない**（FR-16・IADR-0251）。
//   - 手数料・費用: `CostCalculator.EstimateOneWayCost`（`PnlAggregator` と**同じ関数**）
//   - 実現損益: `SignedInventory.Apply`（`PnlAggregator` と**同じ畳み込み**）——在庫が減る約定でのみ計上する
//   - 判断根拠: 監査台帳 `TradeDecisionMade.Rationale` を `DecisionId` で引いて**そのまま**載せる
//
// 🔴 **引けなかったものは `null`（未供給）にする。** 推測で埋めない（レンダラが `**未供給**` と描く）。
public static class TradeHistoryViewBuilder
{
    /// <summary>
    /// 約定列から §2 の明細を組み立てる。
    /// </summary>
    /// <param name="fills">集計対象期間の約定（順序は問わない。約定時刻の昇順へ並べ替える）。</param>
    /// <param name="assumptions">費用の概算に用いる全体前提条件（FR-17）。</param>
    /// <param name="rationales">
    /// `DecisionId` → 記録された判断根拠。<c>null</c> ＝判断記録そのものが未供給（全行が未供給になる）。
    /// 辞書にあるが空文字の根拠、および辞書に無い `DecisionId` は<b>その行だけ</b>未供給になる。
    /// </param>
    public static TradeHistoryView Build(
        IReadOnlyList<PeriodTradeFill> fills,
        TradingAssumptions assumptions,
        IReadOnlyDictionary<Guid, string>? rationales)
    {
        ArgumentNullException.ThrowIfNull(fills);
        ArgumentNullException.ThrowIfNull(assumptions);

        var positions = new Dictionary<(string Symbol, Market Market), InventoryLot>();
        var lines = new List<TradeHistoryLine>(fills.Count);
        var index = 0;

        foreach (var fill in fills.OrderBy(f => f.ExecutedAt))
        {
            index++;

            var key = (fill.Symbol, fill.Market);
            var signedQuantity = fill.Side == TradeSide.Buy ? fill.Quantity : -fill.Quantity;
            positions.TryGetValue(key, out var lot);
            var applied = SignedInventory.Apply(lot, signedQuantity, fill.Price);
            positions[key] = applied.Lot;

            lines.Add(new TradeHistoryLine(
                index,
                // 時刻は JST（報告期間の基準時刻）。レンダラの凡例が基準を明記する。
                TimeOnly.FromTimeSpan(fill.ExecutedAt.ToOffset(ReportSchedule.JstOffset).TimeOfDay),
                fill.Market,
                fill.Symbol,
                // 台帳は銘柄コードのみを持つ（名称の記録源が無い）。
                SymbolName: null,
                fill.Side,
                fill.Quantity,
                fill.Price,
                CostCalculator.EstimateOneWayCost(assumptions, fill.Market, fill.Quantity * fill.Price),
                // 🔴 税は**期間合計にのみ**課される（PnlAggregator）。約定単位へ配分する規則が無いため未供給。
                // ここで 0 と書くと「この約定に税は掛かっていない」と読める。
                Tax: null,
                // 在庫が減らない約定の実現損益は 0（事実）。未供給ではない。
                applied.Reduced ? applied.RealizedPnl : 0m,
                // 🔴 判断の起点は記録されていない（DecisionTriggerKind は取引判断サービスのプロセス内にしか無い）。
                Trigger: null,
                Rationale(rationales, fill.DecisionId)));
        }

        return new TradeHistoryView
        {
            Lines = lines,
            // 🔴 5 項目を分けて持つ記録源が無い＝未供給（空列＝該当なし、ではない）。
            Details = null,
            // 🔴 見送り（Hold）はイベント化されておらずログにしか残らない＝未供給（「見送りなし」ではない）。
            Skipped = null,
        };
    }

    // 相関できない約定（DecisionId が空・辞書に無い）と、空の根拠は未供給にする。**近い記録で代替しない。**
    private static string? Rationale(IReadOnlyDictionary<Guid, string>? rationales, Guid decisionId)
    {
        if (rationales is null || decisionId == Guid.Empty)
            return null;

        return rationales.TryGetValue(decisionId, out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
    }
}
