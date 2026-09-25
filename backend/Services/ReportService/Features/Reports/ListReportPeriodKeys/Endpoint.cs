using AppSvc = ReportService.Features.Reports.ReportAppService;

namespace ReportService.Features.Reports.ListReportPeriodKeys;

// FR-14, FR-07, UC-03〜05, #843 項目1, IADR-0418: 会話キーの一覧（利用者のみ）。Discord の `/report` の入力補完が
// 打鍵ごとに読むため、本文を含む `GET /reports` ではなく**会話キーと開始日だけ**を返す。並びは開始日の降順・同日は
// 会話キーの降順。ページングは持たない（絞り込みは呼び出し側が全キーに対して行う）。
// /{periodKey} より優先される（リテラル一致。/daily-policy・/monthly-bootstrap と同じ）。
internal static class ListReportPeriodKeysEndpoint
{
    public static void MapListReportPeriodKeys(this IEndpointRouteBuilder owner) =>
        owner.MapGet("/period-keys", (AppSvc svc) => Results.Ok(svc.ListPeriodKeys()));
}
