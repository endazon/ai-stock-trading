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
    /// FR-10, ADR-0040 決定1（S1）, #820 の 8 巡目監査, IADR-0344 追記(8) 決定4: S1 で新規建てを武装しようとしたが、
    /// 同一銘柄・同方向に<b>帰属不明の建玉</b>（純額 − Active な保護記録の主張合計 &gt; 0）がある、
    /// またはそれが無いことを確かめられない（建玉を照会できない）。
    /// <para>
    /// S1 の行が守る株数はブローカーの<b>純額</b>からしか測れず、他人の建玉（S2・人手・S0 の発注窓）と
    /// 自分の建玉を区別できない。帰属不明の建玉がある銘柄で武装すると、その建玉を
    /// <b>S1 の損切りラインで売る</b>（監査が実測）。「保護レグを張れない Open では建玉を持たない」
    /// （IADR-0210 決定1）に合わせ、<b>建玉を持たずに見送る</b>。<b>末尾へ追加する</b>（序数 4）。
    /// </para>
    /// </summary>
    UnattributedPosition,
}
