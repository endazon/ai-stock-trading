using AiStockTrading.Shared.Contracts.Events;

namespace ReportService.Domain;

// FR-06, FR-16, #338, ADR-0016 決定15, ADR-0027 決定1・決定4, 04_report-templates 月報 §6.1 / 日報 §4:
// 当期間の借株料の記録（空売りの記録の素材）。
//
// 🔴 **料率が取れなかった日は「0 の計上」ではない**（ADR-0027 決定4）。
// `Accruals`（計上できた日）と `Unavailable`（料率が取れず未計上の日）を**別の列で持つ**——
// 1 つの列へ潰すと、借株コストが安く見える（未計上ぶんが 0 円として合計に混ざる）。
//
// **`null`（本型そのものが無い）＝照会できていない／空の列＝当期間に空売り建玉が無かった。**
public sealed record BorrowFeeRecord(
    IReadOnlyList<BorrowFeeAccrued> Accruals,
    IReadOnlyList<BorrowFeeAccrualUnavailable> Unavailable);

/// <summary>
/// FR-06, #338, 04_report-templates 月報 §6.1: 借株料の集計結果（純関数の出力）。
/// </summary>
/// <param name="TotalUsd">計上できた日の合計（USD）。<b>未計上の日は含まない。</b></param>
/// <param name="BySymbolUsd">銘柄別の合計（USD）と適用年率の最大値。</param>
/// <param name="UnavailableDayCount">料率が取れず未計上だった件数。<b>0 円として合計へ混ぜない。</b></param>
/// <param name="MaxRateAnnual">期間内に適用された年率の最大値（上限 20% との照合用）。計上が無ければ null。</param>
public sealed record BorrowFeeSummary(
    decimal TotalUsd,
    IReadOnlyList<(string Symbol, decimal AmountUsd, decimal MaxRateAnnual)> BySymbolUsd,
    int UnavailableDayCount,
    decimal? MaxRateAnnual);

// FR-06, FR-16, #338, ADR-0016 決定15, ADR-0027: 借株料の集計（純関数・決定的）。
public static class BorrowFeeAggregator
{
    /// <summary>ADR-0016 決定3: 借株料の年率上限（20%）。超過は統制の作動対象である。</summary>
    public const decimal MaxAnnualRate = 0.20m;

    public static BorrowFeeSummary Aggregate(BorrowFeeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var bySymbol = record.Accruals
            .GroupBy(a => a.Symbol, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Symbol: g.Key, AmountUsd: g.Sum(a => a.AmountUsd), MaxRateAnnual: g.Max(a => a.RateAnnual)))
            .ToList();

        return new BorrowFeeSummary(
            record.Accruals.Sum(a => a.AmountUsd),
            [.. bySymbol],
            record.Unavailable.Count,
            // 🔴 計上が 1 件も無い期間で 0 を返さない。「年率 0% が適用された」と読めるため null にする。
            record.Accruals.Count == 0 ? null : record.Accruals.Max(a => a.RateAnnual));
    }
}
