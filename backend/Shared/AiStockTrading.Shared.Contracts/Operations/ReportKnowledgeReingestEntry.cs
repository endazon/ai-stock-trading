namespace AiStockTrading.Shared.Contracts.Operations;

// FR-08, FR-11, #1028, IADR-0436 決定 4: 確定報告書の KB への入れ直しの内訳 1 行（`ReportKnowledgeReingested.Breakdown`）。
// 送らなかった（本文が空・上限超）／失敗（理由）／不明（タイムアウト等）の報告書だけを載せる。
// Outcome は文字列（"SkippedEmptyBody" / "SkippedBodyTooLarge" / "Failed" / "Unknown"）。DocumentId は対象の KB 文書が分かっているときだけ。
//
// 🔴 Events 名前空間に置かない —— `EventTypeDiscovery` は同名前空間の record をすべてドメインイベントとして数え、
// 監査ハンドラと後方互換の基準を要求する（行の型はイベントではない）。
public sealed record ReportKnowledgeReingestEntry(
    string PeriodKey,
    string Outcome,
    string? Reason,
    Guid? DocumentId);
