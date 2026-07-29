namespace AiStockTrading.Shared.Contracts.Events;

// FR-06, FR-07, FR-09, FR-11, UC-03〜05, IADR-0115/0116, #280: 報告書ドラフトが自動生成され、利用者の承認待ち
// （ReviewState.PendingApproval）として提示された。ReportConfirmed（確定）と対になる「提示」イベント。
//
// 発行は報告書サービスの自動生成スケジューラで、**提示まで到達したものだけ**（提示が受理されなかった期間は
// 承認待ち一覧に並ばないため発行しない）。NotificationService が購読して Discord へ確定依頼を投稿し（FR-09）、
// AuditService が中央監査台帳へ集約する（FR-11: 全イベントの時系列記録）。
//
// Kind は ReportConfirmed と同じく文字列（"Daily"/"Weekly"/"Monthly"）。列挙型を wire 契約に晒さず、
// 値の追加で消費側が壊れないようにする。Summary は投稿可能な形へサニタイズ済み（IADR-0116 決定3/4）で、
// 数値はコード集計値のみを含む（LLM に数値を語らせない・FR-16）。Version は提示時点の版番号で、
// 利用者が確定する際の expectedVersion（版番号付き冪等・IADR-0024）として使える。
public record ReportDraftPresented(
    string PeriodKey,
    string Kind,
    string PeriodLabel,
    string Summary,
    int Version,
    DateTimeOffset OccurredAt);
