namespace AiStockTrading.Shared.Contracts.Events;

// FR-02, FR-04, FR-06, FR-11, #337, IADR-0247: スクリーニング入力がコンテキスト予算を超え、
// **縮退（① 銘柄分割 → ② RAG 削除 → ③ ニュース/開示の削除）が発生した**。
//
// 🔴 計画 06_technical/01「判断の二段化」（利用者裁定 2026-08-02・planning#53）:
// 「切り詰めが発生した事実は記録し、月報に件数を記載する。**分割（材料は減らない）と切り詰め
// （材料が減る）は分けて数える**」。ADR-0017 決定4（フォールバック発火の記録）と同じ考え方であり、
// **「静かに判断材料が減っていた」状態を防ぐ**ための記録である。
//
// 監査サービスが台帳へ記録し、月報の期間集計は台帳の種別 × 期間照会で
// 「分割 = Split が真の件数」「切り詰め = Truncated が真の件数」を別々に数える。
public record ScreeningContextReduced(
    IReadOnlyList<string> Symbols,
    int BatchCount,
    bool Split,
    int DroppedRagCount,
    int DroppedNewsCount,
    bool UnresolvableOverflow,
    int BudgetChars,
    DateTimeOffset OccurredAt)
{
    /// <summary>切り詰め（材料が減った）か。**分割（Split）とは別に数える**（planning#53 の裁定）。</summary>
    public bool Truncated => DroppedRagCount + DroppedNewsCount > 0;
}
