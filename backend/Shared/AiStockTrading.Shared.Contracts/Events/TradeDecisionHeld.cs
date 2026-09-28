using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// 🔴 UC-02, FR-03, FR-04, ADR-0003, #1077, IADR-0452 決定1/2: 取引判断サービスが **AI 判断を行い結論を得たが、
// 発注意図を作らなかった**（LLM の Hold、または Buy/Sell の結論を統制が見送らせた）。
//
// 計画は急変の基準点を「前回 AI 判断を行った時点の価格」と定める（04_workflows/02 §補足）。見送りも判断結果である
// （UC-01 事後条件「発注あり/見送り」）。以前は TradeDecisionMade（発注意図あり）だけが基準値を進めたため、
// Hold が続く間は基準値が作られず、UC-02 が一度も発火しなかった（#1077）。市場監視が本イベントで基準値を進める。
//
// 🔴 **判断をしなかった見送り（日報未確定・現在値なし・換算レート未解決・鮮度切れで保有なし）と、
// LLM の出力を解析できなかった場合には出さない**（IADR-0452 決定1。IADR-0248 の「解析不能は見送りと別の事実」）。
// 🔴 **これは発注の経路ではない。** リスク管理は購読しない（TradeDecisionMade に Hold を載せる案は発注意図と
// 誤読されるため採らなかった。IADR-0452 決定2）。
//
//   - Price: 判断時点の価格（現在値 → 起点の価格 → LLM の参照価格の順。IADR-0452 決定3）。常に正。
//   - Reason: 見送りの理由（`DecisionSkipReason` の名前。観測・監査の読み手向けで、購読側は分岐に使わない）。
//   - CycleTrigger: `BusinessMetrics.TriggerScheduled` / `TriggerPriceMovement` の語彙（TradeDecisionMade と同じ）。
public record TradeDecisionHeld(
    Guid EventId,
    string Symbol,
    Market Market,
    decimal Price,
    string Reason,
    DateTimeOffset DecidedAt,
    string? CycleTrigger = null);
