namespace AiStockTrading.Shared.Contracts.Trading;

/// <summary>
/// 🔴 FR-05, ADR-0067 決定5, #1339, IADR-0539: 注文状態の<b>表示</b>（通知・監査の要約・ログ）。列挙名に日本語の区別を添える。
/// <para>
/// <b>失注（<see cref="OrderStatus.Expired"/>）と拒否（<see cref="OrderStatus.Rejected"/>）を取り違えさせない。</b>
/// 失注は期限（DAY）の満了などでブローカーが未約定のまま失効させた注文（moomoo の <c>Disabled</c>）で、時間切れである。
/// 拒否はブローカーが発注を受理しなかった状態で、発注の内容を見直す事象である。対処が違う（FR-05）。
/// </para>
/// <para>
/// 列挙名は残す（ログの grep・既存の運用手順を壊さない）。判定には使わない（表示専用）。
/// </para>
/// </summary>
public static class OrderStatusDisplay
{
    /// <summary>日本語の呼び名（FR-05 の 受付・約定・失注・取消・拒否）。</summary>
    public static string Label(OrderStatus status) => status switch
    {
        OrderStatus.Accepted => "受付",
        OrderStatus.PartiallyFilled => "一部約定",
        OrderStatus.Filled => "全量約定",
        OrderStatus.Expired => "失注: 期限切れで失効",
        OrderStatus.Cancelled => "取消",
        OrderStatus.Rejected => "拒否: 証券会社が受理せず",
        _ => "不明",
    };

    /// <summary>「列挙名（日本語の呼び名）」。例: <c>Expired（失注: 期限切れで失効）</c>。</summary>
    public static string Describe(OrderStatus status) => $"{status}（{Label(status)}）";
}
