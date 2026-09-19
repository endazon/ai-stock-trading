using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-07, FR-14, #840, IADR-0352 決定 5: `GET /reports/{periodKey}/review` の応答。
// 先頭 3 項目は従来の ReportReview と同じ名前・同じ順であり、UnsuppliedInputs だけを足した（非破壊）。
//
// UnsuppliedInputs は**表示名**（ReportInputs.Label のコード定数）。列挙名ではなく表示名を返すのは、
// 読み手（通知サービス）に語彙を二重に持たせないためである（IADR-0240 決定5 が状態 enum の表現へ
// 結合しないのと同じ理由）。
public sealed record ReportReviewView(
    string PeriodKey,
    ReviewState State,
    int Version,
    IReadOnlyList<string> UnsuppliedInputs);
