namespace AiStockTrading.Shared.Contracts.Trading;

/// <summary>
/// FR-11, ADR-0016 決定15, #339: 経費区分の分類（純関数）。
/// <para>
/// 区分と性質の写像の**単一情報源**である。集計・表示・監査要約はすべてここを通す ——
/// 写像を各所で書き直すと、片方だけ直したときに
/// **「配当相当額を配当として扱っていない」ことが一箇所で崩れる**。
/// </para>
/// </summary>
public static class TradeExpenseClassification
{
    /// <summary>
    /// 全区分（<b>enum の宣言順＝計画 FR-11 の列挙順</b>）。
    /// 集計はこの順序で 7 区分ぶんを常に返す（区分の取りこぼしを構造的に無くす）。
    /// </summary>
    public static readonly IReadOnlyList<TradeExpenseCategory> All =
    [
        TradeExpenseCategory.Realized,
        TradeExpenseCategory.BorrowFee,
        TradeExpenseCategory.MarginInterest,
        TradeExpenseCategory.DividendInLieu,
        TradeExpenseCategory.Commission,
        TradeExpenseCategory.Fee,
        TradeExpenseCategory.FxCost,
    ];

    /// <summary>
    /// **費用**の区分（<see cref="TradeExpenseCategory.Realized"/> を除く 6 種）。
    /// <para>
    /// 🔴 <see cref="TradeExpenseCategory.DividendInLieu"/> は**ここに含まれる**。
    /// 配当相当額の支払いは費用（譲渡費用に近い扱い）であり、収入ではない。
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<TradeExpenseCategory> Expenses =
        [.. All.Where(c => NatureOf(c) == TradeExpenseNature.TransferCost)];

    /// <summary>
    /// 区分の性質を返す。
    /// <para>
    /// 未知の値は**例外で落とす**（既定値へ倒さない）—— 区分を増やして写像を足し忘れたとき、
    /// 黙って「実現損益でも費用でもない何か」として集計から漏れるのが最悪の壊れ方である。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">写像が定義されていない区分。</exception>
    public static TradeExpenseNature NatureOf(TradeExpenseCategory category) => category switch
    {
        TradeExpenseCategory.Realized => TradeExpenseNature.RealizedProfitAndLoss,

        // 🔴 DividendInLieu をここへ置くことが「配当と混同しない」の実体である。
        // 収入側の性質は TradeExpenseNature に無く、**混同しようにも置き場が無い**。
        TradeExpenseCategory.BorrowFee
            or TradeExpenseCategory.MarginInterest
            or TradeExpenseCategory.DividendInLieu
            or TradeExpenseCategory.Commission
            or TradeExpenseCategory.Fee
            or TradeExpenseCategory.FxCost => TradeExpenseNature.TransferCost,

        _ => throw new ArgumentOutOfRangeException(
            nameof(category), category, "経費区分の性質が定義されていない（区分を追加したら写像も追加すること）。"),
    };

    /// <summary>その区分が費用か（＝実現損益ではないか）。</summary>
    public static bool IsExpense(TradeExpenseCategory category) =>
        NatureOf(category) == TradeExpenseNature.TransferCost;
}
