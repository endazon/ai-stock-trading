namespace AiStockTrading.Report.Domain;

// FR-06, FR-07, ADR-0017 決定4-(1), #335, IADR-0217:
// 報告書のメタ情報として残す「**実際に使用したモデル**」と、フォールバック発火の事実。
//
// ADR-0017 決定4 は可視化を 3 経路（①報告書メタ ②警告通知 ③月報集計）すべて実施すると定めた。
// 本型は①を担う。**フォールバック発火時はその事実と原因も記録する**（同決定 4-(1)）。
//
// Outcome は `LlmAssignmentOutcome` の名前（"Primary" / "FallbackFired" / "Unassigned" / "Forbidden"）。
// 文字列で持つのは、Domain 層が語彙の増減で壊れないようにするためである（LlmStopReasons と同じ方針）。
public sealed record LlmModelUsage(string Purpose, string? ExpectedModel, string? EffectiveModel, string Outcome)
{
    /// <summary>第 1 候補（ピン）どおりに応答したか。</summary>
    public bool IsPrimary => string.Equals(Outcome, "Primary", StringComparison.Ordinal);
}
