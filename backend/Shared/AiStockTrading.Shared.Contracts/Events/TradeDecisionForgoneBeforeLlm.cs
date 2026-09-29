using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// 🔴 NFR, FR-04, FR-10, FR-11, #1092, IADR-0462 決定4: 取引判断が **LLM を呼ぶ前に**見送った事実（1 回の見送りにつき 1 件）。
//
// LLM が結論を出した後の見送りは TradeDecisionHeld（IADR-0452）が台帳へ出す。それより前の 4 地点は、従来メトリクスとログにしか
// 残らず、Pod の再起動で消えた（#1092）。🔴 TradeDecisionHeld / TradeDecisionSkipped / OrderDispatchForgone は流用しない
// （IADR-0358 決定4: 流用は誤帰属。TradeDecisionHeld は市場監視が急変の基準値を進める事実であり、判断をしていない見送りで
// 基準値を動かしてはならない〔IADR-0452 決定1〕）。
//
//   - Reason: 4 値（`DecisionSkipReason` のうち LLM より前の全部。名前は同じ）。
//   - CycleTrigger: `BusinessMetrics.TriggerScheduled` / `TriggerPriceMovement` の語彙（TradeDecisionHeld と同じ）。
//   - 監査台帳だけが購読する（通知しない。日報の未確定の通知は DailyPolicyUnconfirmed が営業日ごとに出す）。
public record TradeDecisionForgoneBeforeLlm(
    Guid EventId,
    string Symbol,
    Market Market,
    DecisionForgoneBeforeLlmReason Reason,
    DateTimeOffset OccurredAt,
    string? CycleTrigger = null);

/// <summary>
/// NFR, FR-04, #1092, IADR-0462 決定4: LLM を呼ぶ前の見送りの理由。<b>名前は <c>DecisionSkipReason</c> の同名の値と一致させる</b>
/// （試験が固定する）。値を足すときは末尾へ足す。
/// </summary>
public enum DecisionForgoneBeforeLlmReason
{
    /// <summary>FR-07: 確定済み日報の方針が無い。</summary>
    DailyPolicyUnconfirmed,

    /// <summary>FR-02, IADR-0099 決定3: 現在値ソースが有効なのに現在値が取れない・鮮度切れ。</summary>
    CurrentPriceUnavailable,

    /// <summary>FR-10, FR-17, IADR-0107: 基準通貨への換算レートが解決できない。</summary>
    FxRateUnresolved,

    /// <summary>FR-10, ADR-0022 決定5, IADR-0197: 換算レートが鮮度切れで、保有が無い／不明。</summary>
    FxRateStaleNoHolding,
}
