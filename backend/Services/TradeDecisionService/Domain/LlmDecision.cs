namespace TradeDecisionService.Domain;

// FR-04: LLM の売買判断（構造化出力）。Buy/Sell は発注、Hold は見送り（取引しない）。
public enum TradeAction
{
    Hold,
    Buy,
    Sell,
}

// FR-04: LLM の構造化判断。ReferencePrice は 1 株あたり参照価格、StopLossDistancePerShare は損切り幅（サイジング入力）。
// FR-17, IADR-0076: ExpectedProfitPerShare は 1 株あたりの想定利益（費用控除前の見込み値幅）で採算評価（ProfitabilityGate）
// の入力。相場の見込み（判断）は LLM、費用・しきい値の算術はコード（05 §4 採用方針）。既定 0＝想定利益なし（採算ゲート有効時は保守側）。
// Hold の場合は価格・損切り幅・想定利益は用いない。
public record LlmDecision(
    TradeAction Action,
    string Rationale,
    decimal ReferencePrice,
    decimal StopLossDistancePerShare,
    decimal ExpectedProfitPerShare = 0m)
{
    // 安全既定: 取引しない（Hold）。解析不能・不正出力はこれに倒す。
    public static readonly LlmDecision Hold = new(TradeAction.Hold, "解析不能または見送り", 0m, 0m);
}
