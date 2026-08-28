using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-05, FR-10, ADR-0002（OpenD 常駐・SPOF・INDEX 決定 33「再起動中は発注不可」）, #331, IADR-0211:
// 承認済み注文を**発注せずに見送った**。注文はブローカーに存在しないため注文状態
// （OrderStatus。受付・約定・失注・取消・拒否）を持たず、本イベントが唯一の記録である。
//
// - **キューイングしない**: 見送った注文は破棄され、自動では再発注されない（再発注は次の取引判断からのみ）。
// - **「拒否」と別集計**: OrderStatus.Rejected は「証券会社が受理しなかった状態」であり（FR-05・planning#60）、
//   見送り（届いてすらいない）を混ぜると集計が接続障害で汚染される。監査台帳の EventType も別になる。
public record OrderDispatchForgone(
    Guid DecisionId,
    OrderIntent Intent,
    OrderDispatchForgoneReason Reason,
    DateTimeOffset OccurredAt);

// #331, IADR-0210 決定1 / IADR-0211: 見送りの理由。いずれも**発注前**に確定する。
public enum OrderDispatchForgoneReason
{
    /// <summary>ブローカー（OpenD）へ到達できない（接続確立の失敗＝確実に未発注）。ADR-0002/0024 の SPOF。</summary>
    BrokerUnavailable,

    /// <summary>Open 注文に損切り価格（StopLossPrice）が無い。逆指値を張れない建玉は持たない（FR-10）。</summary>
    StopLossPriceMissing,

    /// <summary>ブローカーが逆指値の発注能力（IProtectiveOrderBroker）を持たない。同上（fail-closed）。</summary>
    StopOrderUnsupported,
}
