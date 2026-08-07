namespace AiStockTrading.RiskManagement.Domain;

// FR-10, UC-06, ADR-0016 決定4/決定10, #374, #419, IADR-0159 決定5:
// 強制買戻し（buy-in）由来の 30 日空売り禁止が有効かを判定する**単一情報源**。
//
// 同じ規則を 2 か所に書かないために存在する——判定の入口が 2 つあるためである。
//   1. <c>ShortSellOrderContext.BuyInBanUntil</c>（借株照会ほかを含む完全な文脈が組める場合）
//   2. <c>BuyInBanSupply</c>（**文脈が組めなくても禁止期限だけは供給できる**場合。現況はこちら）
//
// 期限は**日付でのみ**失効する。観測が届かない・照会が失敗した等の理由で禁止が解けてはならない
// （解けると fail-open になる。IADR-0159 決定4）。
public static class BuyInBanPolicy
{
    /// <summary>
    /// FR-10, ADR-0016 決定4: 判定日 <paramref name="today"/> が禁止期限 <paramref name="banUntil"/> より前なら
    /// 禁止期間中である（解除日当日は禁止しない＝<c>ShortSellingLimits.BuyInBanUntil</c> が当日を含まない期限を返すのと対）。
    /// <paramref name="banUntil"/> が <c>null</c>（禁止なし）なら常に <c>false</c>。
    /// </summary>
    public static bool IsBanned(DateOnly today, DateOnly? banUntil) =>
        banUntil is { } until && today < until;
}
