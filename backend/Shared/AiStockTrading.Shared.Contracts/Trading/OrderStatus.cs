namespace AiStockTrading.Shared.Contracts.Trading;

// FR-05: 注文状態（受付・約定・失注・取消・拒否）の追跡
public enum OrderStatus
{
    Accepted,
    PartiallyFilled,
    Filled,
    Expired,
    Cancelled,

    /// <summary>
    /// 証券会社側による注文拒否（資金不足・値幅制限・不正な注文内容等）。moomoo 実発注では通常発生し得る。
    /// リスク管理サービスによる発注前の事前拒否（<see cref="Events.OrderRejected"/> イベント）とは別物で、
    /// こちらは注文がブローカーへ到達した後にブローカーが拒否した終端状態を表す。
    /// <para>
    /// FR-05, #832, IADR-0211（2026-09-25 追記）, IADR-0346 決定5: <b>唯一の例外</b>はリスク管理の内部射影
    /// <c>order_activity</c> である。見送り（<see cref="Events.OrderDispatchForgone"/>＝ブローカーへ発注していない）を
    /// 本値で終端化する（相場操縦検知が約定なし取消の母集団から外すため）。その値は <c>OrderExecuted</c> として発行されず、
    /// FR-05 の拒否の集計（監査台帳ほか）は <c>order_activity</c> を読まない。<b>契約イベント上の本値は常に証券会社の拒否である。</b>
    /// </para>
    /// </summary>
    Rejected,
}
