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
    /// </summary>
    Rejected,
}
