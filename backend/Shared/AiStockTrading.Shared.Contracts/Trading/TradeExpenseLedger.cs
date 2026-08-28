namespace AiStockTrading.Shared.Contracts.Trading;

/// <summary>
/// FR-11, ADR-0016 決定15, ADR-0027 決定2, #339: 経費明細を**建玉単位**へ畳む純関数。
/// <para>
/// 集計機能そのもの（FR-18）は対象外である。ここに置くのは
/// 「**後から集計可能な粒度で記録できているか**」を型と関数で示すための最小の導出であり、
/// 明細（<see cref="TradeExpense"/>）から機械的に得られる値しか返さない。
/// </para>
/// <para>
/// 🔴 <b>銘柄別・口座全体を別に積まない。</b> ADR-0027 決定2 が「建玉ごとに積み、銘柄・口座へは
/// <b>合算で導出する</b>」と定めている。導出でしか得られない形にしておくと、
/// 積む側と合計側が食い違う経路そのものが生まれない。
/// </para>
/// </summary>
public static class TradeExpenseLedger
{
    /// <summary>
    /// 建玉（<c>(Symbol, Market)</c>）ごとの集計を返す。
    /// <para>順序は銘柄（序数比較）→市場で決定的に解く（入力の並び順に依存しない）。</para>
    /// </summary>
    public static IReadOnlyList<PositionExpenseSummary> SummarizeByPosition(
        IReadOnlyList<TradeExpense> expenses)
    {
        ArgumentNullException.ThrowIfNull(expenses);

        return [.. expenses
            .GroupBy(e => (e.Symbol, e.Market))
            .OrderBy(g => g.Key.Symbol, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Market)
            .Select(g => Build(g.Key.Symbol, g.Key.Market, [.. g]))];
    }

    /// <summary>
    /// 建玉 1 件の集計を返す。
    /// <para>
    /// 明細が 1 件も無くても**空を返さない** —— 7 区分すべてを
    /// <see cref="TradeExpenseCategoryTotal.LineCount"/> = 0 で返す。
    /// 「照会できなかった」ではなく「1 件も計上されていない」ことが呼び出し側で読めるようにする。
    /// </para>
    /// </summary>
    public static PositionExpenseSummary SummarizePosition(
        IReadOnlyList<TradeExpense> expenses,
        string symbol,
        Market market)
    {
        ArgumentNullException.ThrowIfNull(expenses);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        return Build(
            symbol,
            market,
            [.. expenses.Where(e => e.Symbol == symbol && e.Market == market)]);
    }

    // 7 区分ぶんを常に作る。明細のある区分だけを返すと、呼び出し側が存在しないキーを引いて黙って 0 を得る。
    private static PositionExpenseSummary Build(
        string symbol,
        Market market,
        IReadOnlyList<TradeExpense> lines) =>
        new(
            symbol,
            market,
            [.. TradeExpenseClassification.All.Select(category =>
            {
                var forCategory = lines.Where(l => l.Category == category).ToList();
                return new TradeExpenseCategoryTotal(
                    category, forCategory.Sum(l => l.AmountUsd), forCategory.Count);
            })]);
}
