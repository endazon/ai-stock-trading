using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-06, FR-16, #563, IADR-0268: 日報 §3「ポジション一覧（当日終了時点）」の建玉を供給するポート。
// 権威源はリスク管理サービスの取引台帳の射影（GET /risk-controls/open-positions・OwnerOrService・IADR-0051）。
//
// 🔴 **期間を引数に取らない。** 台帳は建玉のスナップショット履歴を持たず、射影は**照会時点**の建玉を返す。
// 報告書の生成は当該取引日の生成境界の直後に走る（IADR-0115）ため実務上は「当日終了時点」に一致するが、
// **過去の期間を後から再生成した場合は当時の建玉を復元できない**——引数で期間を受け取れる形にすると、
// 期間で絞れているかのように読めてしまうため、受け取らない。
//
// 🔴 **供給不達は `null`（未供給）へ倒す。空列（建玉なし）と混ぜない**——
// 「今は何も持っていない」は重い事実であり、照会できなかったことと同じに書かない。
public interface IOpenPositionSource
{
    /// <summary>
    /// 照会時点の建玉を返す。取得不能なら <c>null</c>（例外を投げない）。
    /// 現在値・評価損益・借株料累計・保有日数は本ポートでは供給されない（<c>null</c> のまま返す）。
    /// </summary>
    Task<IReadOnlyList<ReportPosition>?> GetOpenPositionsAsync(CancellationToken cancellationToken = default);
}
