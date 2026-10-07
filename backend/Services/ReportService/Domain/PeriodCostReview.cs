using AiStockTrading.Shared.Kernel.Trading;

namespace ReportService.Domain;

// FR-06, FR-07, FR-16, FR-17, 04_report-templates §数値の定義「費用合計」, 計画 ADR-0035 決定 3・4, #1201, IADR-0501:
// 報告書の**費用合計**（純関数の値）。
//
// 費用合計 ＝ 売買手数料 ＋ 取引諸費用 ＋ 為替スプレッド相当額 ＋ 借株料（**税は含めない**）。
//
// 🔴 **未供給の区分を 0 として積まない**（計画「当該区分を『未供給』と描き、費用合計が過小である旨を凡例へ明記する」）。
// 合計は**供給された区分の和**であり、未供給・未計上があれば <see cref="IsUnderstated"/> が立つ。描画は過小である旨を必ず書く。
//
// 🔴 **為替スプレッドは事後集計の実績**（入出金時の両替。決定 4）であり、取引判断の事前見積り
// （<see cref="CostCalculator.EstimateOneWayCostBreakdown"/> の <c>FxSpread</c>）とは**別の値**である。
// 入出金の両替の実績を本サービスが受け取る経路が無いため、現在は常に <c>null</c>（未供給）になる。
/// <summary>
/// 期間の費用合計（04_report-templates §数値の定義）。<b>§1 サマリ・週報 §5・月報 §1 が同じ値を描く。</b>
/// </summary>
/// <param name="TradingCost">売買手数料＋取引諸費用（<see cref="PnlSummary.TotalCost"/>。約定ごとに <see cref="CostCalculator.FillCost"/>）。</param>
/// <param name="FxSpread">為替スプレッド相当額（入出金時の両替の実績）。<c>null</c>＝未供給。</param>
/// <param name="BorrowFee">借株料（計上できた日の合計・USD）。<c>null</c>＝照会できていない（未供給）。</param>
/// <param name="BorrowFeeUnrecordedCount">料率が取れず未計上の件数（ADR-0027 決定4。0 円として合計へ混ぜない）。</param>
public sealed record PeriodCostTotal(
    decimal TradingCost,
    decimal? FxSpread,
    decimal? BorrowFee,
    int BorrowFeeUnrecordedCount)
{
    /// <summary>供給された区分の和。<b>未供給の区分は含まない</b>（0 を積まない）。</summary>
    public decimal Amount => TradingCost + (FxSpread ?? 0m) + (BorrowFee ?? 0m);

    /// <summary>未供給の区分、または未計上の借株料があり、<see cref="Amount"/> が実際より過小である。</summary>
    public bool IsUnderstated => FxSpread is null || BorrowFee is null || BorrowFeeUnrecordedCount > 0;

    /// <summary>
    /// 費用合計を組み立てる（純関数）。
    /// </summary>
    /// <param name="tradingCost">売買手数料＋取引諸費用の期間合計。</param>
    /// <param name="borrowFees">借株料の記録（<c>null</c>＝照会できていない）。</param>
    public static PeriodCostTotal From(decimal tradingCost, BorrowFeeRecord? borrowFees)
    {
        var borrow = borrowFees is { } record ? BorrowFeeAggregator.Aggregate(record) : null;

        return new PeriodCostTotal(
            tradingCost,
            // 計画 ADR-0035 決定 4: 為替スプレッドは入出金時の両替にだけ掛かる。**約定ごとの見積りで埋めない。**
            // 入出金の両替の実績の供給元が無いため未供給（null）。供給元ができたら引数で受け取る。
            FxSpread: null,
            borrow?.TotalUsd,
            borrow?.UnavailableDayCount ?? 0);
    }
}

// FR-06, FR-07, FR-16, FR-17, 04_report-templates 週報 §5「リスク・費用レビュー」・月報 §1「費用合計 / 費用率」,
// #615, IADR-0305, 計画 ADR-0035 決定 1〜5, #1201, IADR-0501:
// 期間の**費用レビュー**（純関数）。費用の内訳（手数料・諸費用・為替スプレッド・借株料）と費用率を作る。
//
// 🔴 **期間を切って PnlAggregator.Aggregate を呼び直さない**（IADR-0301 決定1）。入力は
// 期間全体を 1 回だけ畳み込んだ約定単位の帰属（FillPnlAttribution）であり、内訳は**それを数え直すだけ**である。

/// <summary>
/// 04_report-templates 週報 §5・月報 §1 の費用レビュー。<b>数値はコード集計値であり LLM に作らせない</b>（FR-16）。
/// </summary>
/// <param name="Commission">売買手数料の合計（<see cref="CostCalculator.FillCost"/> の Commission 項）。</param>
/// <param name="RegulatoryFees">
/// 取引諸費用の合計（同 RegulatoryFees 項＝米国株の売り約定の SEC Section 31 手数料・FINRA 取引活動料）。
/// 料率は前提条件の設定点（計画 05_trading-assumptions §2 の暫定値）から読む。
/// </param>
/// <param name="Total">
/// 費用合計（<see cref="PeriodCostTotal"/>）。<b>§1 サマリの「費用合計」と一致する</b>——
/// 同じ約定・同じ費用関数・同じ畳み込みから数え、同じ借株料の記録を足しているためである（テストで固定する）。
/// </param>
/// <param name="TaxWithheld">
/// 源泉徴収税額。<b>帰属からは出せない</b>——税は<b>期間合計にのみ</b>課され、約定単位へ配分する規則が無い
/// （日報 §2 の税列が未供給なのと同じ理由）。<see cref="PnlSummary"/> の値をそのまま持つ。
/// </param>
/// <param name="TradeValueDifference">
/// 費用率の<b>分母</b>＝<b>約定代金差額</b>（売却代金 − 取得代金。費用・税をいずれも控除しない値。計画 ADR-0035 決定 1）。
/// 🔴 「実現損益（税引前・費用前）」とは呼ばない——実現損益は税引後・費用込みと定義済みであり、同じ語に 2 つの意味を持たせない。
/// </param>
/// <param name="CostRatio">
/// 費用率（<c>Total.Amount / TradeValueDifference</c>）。
/// <para>
/// 🔴 <c>null</c> は<b>「算出不能」であり「未供給」ではない</b>——分母が 0 以下（損失の期間・約定が無い期間）
/// のとき、比率は意味を持たない（計画 ADR-0035 決定 2）。<b>0% と書かない</b>（負の分母で割ると符号が反転し、
/// 「費用が少ない期間」に見える）。
/// </para>
/// </param>
public sealed record PeriodCostReview(
    decimal Commission,
    decimal RegulatoryFees,
    PeriodCostTotal Total,
    decimal TaxWithheld,
    decimal TradeValueDifference,
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
    /// <param name="borrowFees">期間の借株料の記録（<c>null</c>＝照会できていない。§1 と同じ値を渡す）。</param>
    public static PeriodCostReview Build(
        IReadOnlyList<FillPnlAttribution> entries,
        TradingAssumptions assumptions,
        decimal taxWithheld,
        BorrowFeeRecord? borrowFees)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(assumptions);

        var commission = 0m;
        var regulatoryFees = 0m;
        var tradeValueDifference = 0m;

        foreach (var e in entries)
        {
            // 🔴 **費用関数は PnlAggregator / FillPnlAttributionBuilder と同一**（CostCalculator.FillCost）。
            // 別式で分解すると、内訳の合計が費用合計と一致しなくなる。
            var breakdown = CostCalculator.FillCost(assumptions, e.Market, e.Side, e.Quantity, e.Price);
            commission += breakdown.Commission;
            regulatoryFees += breakdown.RegulatoryFees;
            tradeValueDifference += e.RealizedPnlGross;
        }

        var total = PeriodCostTotal.From(commission + regulatoryFees, borrowFees);

        return new PeriodCostReview(
            commission,
            regulatoryFees,
            total,
            taxWithheld,
            tradeValueDifference,
            // 分母 ≤ 0 は「算出不能」（null）。**0 で埋めない**（「費用が掛かっていない」と読める）。
            tradeValueDifference > 0m ? total.Amount / tradeValueDifference : null);
    }
}
