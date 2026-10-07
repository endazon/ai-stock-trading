using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// 🔴 NFR, FR-04, FR-10, FR-11, #1092, IADR-0462 決定4: 取引判断が **LLM を呼ぶ前に**見送った事実（1 回の見送りにつき 1 件）。
//
// LLM が結論を出した後の見送りは TradeDecisionHeld（IADR-0452）が台帳へ出す。それより前の 4 地点は、従来メトリクスとログにしか
// 残らず、Pod の再起動で消えた（#1092）。🔴 TradeDecisionHeld / TradeDecisionSkipped / OrderDispatchForgone は流用しない
// （IADR-0358 決定4: 流用は誤帰属。TradeDecisionHeld は市場監視が急変の基準値を進める事実であり、判断をしていない見送りで
// 基準値を動かしてはならない〔IADR-0452 決定1〕）。
//
//   - Reason: 6 値（`DecisionSkipReason` のうち LLM より前の全部。名前は同じ）。#1113 / IADR-0463 で EntryBlockedByRiskControls を、
//     #1176 / IADR-0495 で EntryCapacityBelowMinimumNotional を末尾へ足した。
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

    /// <summary>
    /// FR-10, FR-04, #1113, IADR-0463 決定 4: 保有が既知で 0・未約定の新規建てが既知で空の銘柄で、リスク管理の新規建ての可否の口が
    /// 買いの新規建てを**審査で必ず拒否される**と答えた（kill switch・一時停止・当日の損切り・保有建玉数・日次損失・最大 DD・GFV）。
    /// 審査の拒否（<c>ast.risk.rejections</c>）の一部がここへ移る。
    /// </summary>
    EntryBlockedByRiskControls,

    /// <summary>
    /// FR-10, #1176, IADR-0495 決定1・2: 保有が既知で 0・未約定の新規建てが既知で空の銘柄で、新規建てに使える金額の上限
    /// （1 注文上限と、段階残枠・日次残枠の小さい方の、さらに小さい方）が**最小の名目額（equity × しきい値。既定 1%）に届かない**。
    /// サイジングの株数は LLM の後でしか決まらないが、名目額はこの上限を超えないため、LLM の結論に依らず新規建ては必ず見送られる。
    /// </summary>
    EntryCapacityBelowMinimumNotional,
}
