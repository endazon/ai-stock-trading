using AiStockTrading.Shared.Contracts.Events;

namespace AiStockTrading.Report.Application.Ports;

// FR-10, FR-06, UC-06, #330, IADR-0133 決定7: 期間内に発動した「維持率割れによる自動縮小」を供給するポート
// （04_report-templates 日報 §4・月報 §6）。
//
// **供給不達を空列へ倒さない。** 約定の供給（IPeriodFillSource）は欠測を空列＝「数値 0 の報告書」へ倒すが、
// 本記録は「発動があったのに『なし』と書く」ことが**発動を隠したのと同じ結果**になる。したがって
// 取得できなかった場合は <c>null</c> を返し、描画側が「照会できませんでした（要確認）」と明記する。
//
// 権威源（監査台帳ないしリスク管理サービスの照会 API）への結線は、発火元（維持率の供給・#331 / #342）と
// 同時に行う。それまでの既定は空列＝「発動なし」であり、**発動があり得ない現状では事実として正しい**。
public interface IMarginReductionRecordSource
{
    /// <summary>
    /// 期間 [fromInclusive, toInclusive]（JST 取引日）の発動記録。発動なしは空列、**取得不能は null**。
    /// </summary>
    Task<IReadOnlyList<MaintenanceMarginReductionExecuted>?> GetReductionsAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken = default);
}
