using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Domain;

// #270, FR-05, IADR-0113: 注文状態の終端判定。約定追跡（ポーラー）と発注結果ストアが同じ定義を用いるための
// 単一情報源。終端＝これ以上ブローカ側で状態が変わらない状態であり、追跡の打ち切り条件になる。
public static class OrderStatusLifecycle
{
    public static bool IsTerminal(OrderStatus status) =>
        status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Expired;

    /// <summary>非終端（<see cref="OrderStatus.Accepted"/> / <see cref="OrderStatus.PartiallyFilled"/>）＝追跡対象。</summary>
    public static bool IsPending(OrderStatus status) => !IsTerminal(status);

    /// <summary>
    /// 🔴 FR-10, UC-06, #847, IADR-0357, IADR-0117（2026-09-19 追記・改定 2）:
    /// <b>未約定残が二度と約定しない</b>状態（<see cref="OrderStatus.Filled"/> を<b>含まない</b>）。
    /// <para>
    /// 取消の<b>確認</b>に使う —— 取消を送ったあとの照会がこの状態を返したときだけ
    /// 「確実に取り消せた」とみなし、取引台帳の在庫の押さえを解く <c>OrderCancelled</c> を出してよい。
    /// 全量約定（<see cref="OrderStatus.Filled"/>）は「取り消せた」ではないので含めない。
    /// </para>
    /// <para>
    /// 🔴 <see cref="IsTerminal"/> を書き換えて片方へ寄せない。<b>問いが違う</b> ——
    /// あちらは「板から消えたか（＝約定追跡を打ち切ってよいか）」、こちらは「未約定残が生き残っているか」である。
    /// リスク管理サービス側にも同名・同義の述語があるが、<b>サービス境界を越えて参照しない</b>
    /// （共有する契約は <c>OrderStatus</c> 列挙そのものであり、その解釈は各サービスが自分で持つ）。
    /// </para>
    /// </summary>
    public static bool AbandonsUnfilledRemainder(OrderStatus status) =>
        status is OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Expired;
}
