namespace AiStockTrading.TradeDecision.Domain;

// FR-04: LLM の売買判断（構造化出力）。Buy/Sell は発注、Hold は見送り（取引しない）。
public enum TradeAction
{
    Hold,
    Buy,
    Sell,
}

// FR-04: LLM の構造化判断。ReferencePrice は 1 株あたり参照価格、StopLossDistancePerShare は損切り幅（サイジング入力）。
// Hold の場合は価格・損切り幅は用いない。
public record LlmDecision(
    TradeAction Action,
    string Rationale,
    decimal ReferencePrice,
    decimal StopLossDistancePerShare)
{
    // 安全既定: 取引しない（Hold）。解析不能・不正出力はこれに倒す。
    public static readonly LlmDecision Hold = new(TradeAction.Hold, "解析不能または見送り", 0m, 0m);
}
