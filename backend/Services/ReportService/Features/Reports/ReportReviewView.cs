using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-07, FR-14, #840, IADR-0352 決定 5: `GET /reports/{periodKey}/review` の応答。
// 先頭 3 項目は従来の ReportReview と同じ名前・同じ順であり、UnsuppliedInputs だけを足した（非破壊）。
//
// UnsuppliedInputs は**表示名**（ReportInputs.Label のコード定数）。列挙名ではなく表示名を返すのは、
// 読み手（通知サービス）に語彙を二重に持たせないためである（IADR-0240 決定5 が状態 enum の表現へ
// 結合しないのと同じ理由）。
//
// FR-07, ADR-0068 決定1, #1373, IADR-0549 決定3: 行が確定済みの日報に版を足したドラフト（版 n+1）のとき、ConfirmedVersion は
// いま効いている同じ期間キーの確定版の版番号、NoLongerEffective は効いている日報が移って確定できないこと（どちらも非破壊の追加）。
// 版 n+1 でなければ ConfirmedVersion は null・NoLongerEffective は false。
//
// FR-07, FR-14, #1385, IADR-0556 決定 3: LatestConfirmedVersion は**この期間キーの最新の確定版の版番号**（確定版が無ければ null）。
// 確定は版を 1 進める（下書き v → 確定 v+1）ので、呼び出し側は `LatestConfirmedVersion == v + 1` で「下書きの版 v が確定された」と
// 状態 enum に結合せずに判定できる（確定の応答が時間切れになったときの照会に使う）。非破壊の追加。
public sealed record ReportReviewView(
    string PeriodKey,
    ReviewState State,
    int Version,
    IReadOnlyList<string> UnsuppliedInputs,
    int? ConfirmedVersion = null,
    bool NoLongerEffective = false,
    int? LatestConfirmedVersion = null);
