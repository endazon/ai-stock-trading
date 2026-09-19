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

// FR-06, FR-09, #840, #866, IADR-0352 決定 5: 要約（Summary）に埋め込まれる**印**。
//
// 発行側（報告書サービスの ReportSummary）と消費側（通知サービスの整形）が**同じ定数**を引くために
// 契約アセンブリへ置く。通知サービスは報告書サービスの型を参照できず、かといってイベントへ
// フィールドを足すと旧版の発行側との読み分けが要る（IADR-0352 決定 5 は「契約の形は変えない」）。
// 印は要約の本文の一部であり、**イベントの形（レコードのプロパティ）は 1 つも変えていない**。
public static class ReportSummaryMarkers
{
    /// <summary>未供給の入力があるときに要約へ足す警告行の先頭。これを含む要約の提示は Warning で通知する。</summary>
    public const string UnsuppliedWarningPrefix = "⚠ 未供給の入力があります";
}
