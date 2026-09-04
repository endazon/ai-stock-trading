using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace ReportService.Domain;

// FR-06, FR-07, FR-16, FR-17, 04_report-templates 週報 §5「リスク・費用レビュー」, #615, IADR-0305:
// 期間の**費用レビュー**（純関数）。費用の内訳（手数料・為替スプレッド）と、損益に対する費用率を作る。
//
// 🔴 **期間を切って PnlAggregator.Aggregate を呼び直さない**（IADR-0301 決定1）。入力は
// 期間全体を 1 回だけ畳み込んだ約定単位の帰属（FillPnlAttribution）であり、内訳は**それを数え直すだけ**である。
// 帰属は Quantity・Price・Market を持つため、**フィールドを 1 つも増やさずに**費用を区分へ分解できる。
//
// 🔴 **計画が求める「諸費用」の区分は本型に無い。** 05_trading-assumptions §2 の「米国株 売却時諸費用
//（SEC Fee・TAF 等）」は計画側で **要確認** のままで、前提条件にも設定点が無い。**0 として持たない**ことで、
// 「諸費用が 0 円だった」と読める形を構造的に作らない（描画側は未供給と書く）。

/// <summary>
/// 04_report-templates 週報 §5 の費用レビュー。<b>数値はコード集計値であり LLM に作らせない</b>（FR-16）。
/// </summary>
/// <param name="Commission">売買手数料の合計（<see cref="CostCalculator"/> の commission 項）。</param>
/// <param name="FxSpread">為替スプレッド相当額の合計（同 fxSpread 項。基準通貨の市場では 0）。</param>
/// <param name="TotalCost">
/// 費用合計（<c>Commission + FxSpread</c>）。<b>§1 サマリの「費用合計」と一致する</b>——
/// 同じ約定・同じ費用関数・同じ畳み込みから数えているためである（テストで固定する）。
/// <para>🔴 <b>諸費用は含まれない</b>（記録源が無い）。したがって<b>実際の費用より過小である</b>。</para>
/// </param>
/// <param name="TaxWithheld">
/// 源泉徴収税額。<b>帰属からは出せない</b>——税は<b>期間合計にのみ</b>課され、約定単位へ配分する規則が無い
/// （日報 §2 の税列が未供給なのと同じ理由）。<see cref="PnlSummary"/> の値をそのまま持つ。
/// </param>
/// <param name="RealizedPnlGross">
/// 費用率の<b>分母</b>＝実現損益（<b>税引前・費用前</b>）。<b>本文へ明示的に出す</b>——
/// 計画は「損益に対する費用率」としか書いておらず、分母の定義は実装が定めたものだからである。
/// </param>
/// <param name="CostRatio">
/// 損益に対する費用率（<c>TotalCost / RealizedPnlGross</c>）。
/// <para>
/// 🔴 <c>null</c> は<b>「算出不能」であり「未供給」ではない</b>——分母が 0 以下（損失の期間・約定が無い期間）
/// のとき、比率は意味を持たない。<b>0% と書かない</b>（負の分母で割ると符号が反転し、
/// 「費用が少ない期間」に見える）。
/// </para>
/// </param>
public sealed record PeriodCostReview(
    decimal Commission,
    decimal FxSpread,
    decimal TotalCost,
    decimal TaxWithheld,
    decimal RealizedPnlGross,
    decimal? CostRatio);

public static class PeriodCostReviewBuilder
{
    /// <summary>
    /// 約定単位の損益帰属から期間の費用レビューを作る（純関数・決定的）。
    /// </summary>
    /// <param name="entries">
    /// 期間全体を 1 回だけ畳み込んだ帰属（<see cref="FillPnlAttributionBuilder.Build"/> の出力）。
    /// 🔴 <b>スライスした帰属を渡さない</b>——内訳の合計が §1 サマリと一致しなくなる。
    /// </param>
    /// <param name="assumptions">費用の区分へ分解するための全体前提条件（FR-17）。</param>
    /// <param name="taxWithheld">期間合計の源泉徴収税額（<see cref="PnlSummary.TaxWithheld"/>）。</param>
    public static PeriodCostReview Build(
        IReadOnlyList<FillPnlAttribution> entries,
        TradingAssumptions assumptions,
        decimal taxWithheld)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(assumptions);

        var commission = 0m;
        var fxSpread = 0m;
        var realizedGross = 0m;

        foreach (var e in entries)
        {
            // 🔴 **費用関数は PnlAggregator / FillPnlAttributionBuilder と同一**（CostCalculator）。
            // 別式で分解すると、内訳の合計が費用合計と一致しなくなる。
            var breakdown = CostCalculator.EstimateOneWayCostBreakdown(assumptions, e.Market, e.Quantity * e.Price);
            commission += breakdown.Commission;
            fxSpread += breakdown.FxSpread;
            realizedGross += e.RealizedPnlGross;
        }

        var totalCost = commission + fxSpread;

        return new PeriodCostReview(
            commission,
            fxSpread,
            totalCost,
            taxWithheld,
            realizedGross,
            // 分母 ≤ 0 は「算出不能」（null）。**0 で埋めない**（「費用が掛かっていない」と読める）。
            realizedGross > 0m ? totalCost / realizedGross : null);
    }
}
