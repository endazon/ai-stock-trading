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

    /// <summary>
    /// FR-10, ADR-0040 決定1, #819, IADR-0342 決定4: 承認が S0 以外の損切り実行機構を運んでいるのに、
    /// 発注先が moomoo SIMULATE ではない（実弾・内蔵 paper）。<b>S0 以外は SIMULATE でしか選べない</b>ため、
    /// 読み替えずに発注しない（fail-closed）。<b>末尾へ追加する</b>（序数 3。メトリクスのタグ・監査 payload の整数が往来する）。
    /// </summary>
    StopLossMethodNotPermitted,

    /// <summary>
    /// 🔴 FR-10, FR-05, ADR-0016, #864, IADR-0355 決定2: 決済（Close）だが、<b>ブローカーに決済方向の建玉が
    /// 1 株も無い</b>。台帳の建玉は実在せず（#849 の乖離）、送れば<b>保有 0 からの売り＝裸の新規ショート</b>になる。
    /// 空売りは方針で禁止であり、決済として通る注文にはショート建玉の規律も空売り統制も効かない。
    /// <b>末尾へ追加する</b>（序数 4。メトリクスのタグ・監査 payload の整数が往来する）。
    /// </summary>
    BrokerPositionAbsent,

    /// <summary>
    /// 🔴 FR-10, FR-05, ADR-0016, #864, IADR-0355 決定3: 決済（Close）だが、<b>ブローカーの建玉を照会できない
    /// （null＝不明）</b>。空列（建玉ゼロ）と取り違えず、不明のままでは送らない（fail-closed）。
    /// 選ばなかった側（不明でも送る）の害は IADR-0355 決定3 に明記した。<b>末尾へ追加する</b>（序数 5）。
    /// </summary>
    BrokerPositionsIndeterminate,
}
