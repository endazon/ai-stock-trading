namespace AiStockTrading.Shared.Contracts.Trading;

/// <summary>
/// FR-11, ADR-0016 決定15, ADR-0027 決定2, #339: **取引記録の経費 1 行**（経費台帳の 1 明細）。
/// <para>
/// <b>建玉単位で紐づく。</b> 建玉の一次識別子は <c>(Symbol, Market)</c> の組であり（ADR-0027 決定2）、
/// 銘柄別・口座全体の値は**合算で導出する** —— 別に積むストアは作らない。
/// </para>
/// <para>
/// 🔴 <b>符号の約束。</b> 費用（<see cref="TradeExpenseNature.TransferCost"/> の 6 区分）は
/// <b>費用額を正で</b>持つ。<see cref="TradeExpenseCategory.Realized"/> のみ符号付きで、損失は負である。
/// 費用を負で書くと「費用が戻った」ように読め、合計が実費より小さく出る。
/// </para>
/// </summary>
/// <param name="Symbol">銘柄コード。</param>
/// <param name="Market">市場。<paramref name="Symbol"/> との組が建玉の一次識別子である。</param>
/// <param name="Category">経費区分（7 種）。</param>
/// <param name="AmountUsd">金額（USD）。統制・集計の基準通貨に揃える（ADR-0016 決定6）。</param>
/// <param name="OccurredOn">
/// 帰属日。**按分しない**（ADR-0027 決定3 と同じ扱い）—— この日の属する日・月へ帰属する。
/// </param>
/// <param name="SourceId">
/// 発生元の識別子（注文 ID・日次計上のキー等）。同じ発生元の二重計上を見分けるために持つ。
/// 🔴 <b>秘匿情報を入れない。</b> 監査台帳はイベント全量を JSON で 7 年保持するため、
/// ここへ入れた文字列は 7 年残る。
/// </param>
/// <param name="RecordedAt">台帳へ記録した時刻。</param>
public sealed record TradeExpense(
    string Symbol,
    Market Market,
    TradeExpenseCategory Category,
    decimal AmountUsd,
    DateOnly OccurredOn,
    string SourceId,
    DateTimeOffset RecordedAt);

/// <summary>
/// FR-11, #339: 1 区分ぶんの合計。
/// <para>
/// 🔴 <b><see cref="LineCount"/> を必ず一緒に運ぶ。</b> 金額だけを返すと
/// 「0 円だった（＝実際に費用が発生しなかった）」と「1 件も計上されていない（＝供給が無い）」が
/// 区別できなくなる —— 借株料で同じ誤読を塞いだのと同じ構造である（ADR-0027 決定4）。
/// </para>
/// </summary>
/// <param name="Category">経費区分。</param>
/// <param name="AmountUsd">その区分の合計（USD）。</param>
/// <param name="LineCount">その区分の明細件数。0 なら**未計上**であり「0 円」ではない。</param>
public sealed record TradeExpenseCategoryTotal(
    TradeExpenseCategory Category,
    decimal AmountUsd,
    int LineCount)
{
    /// <summary>明細が 1 件以上あるか（0 円と未計上を呼び出し側が区別するための述語）。</summary>
    public bool HasLines => LineCount > 0;
}

/// <summary>
/// FR-11, ADR-0016 決定15, #339: **建玉 1 件**の経費集計。
/// <para>
/// <see cref="Totals"/> は**常に 7 区分ぶん**を <see cref="TradeExpenseClassification.All"/> の順で持つ。
/// 明細のある区分だけを返すと、呼び出し側が存在しないキーを引いて**黙って 0 を得る**経路ができる。
/// </para>
/// </summary>
/// <param name="Symbol">銘柄コード。</param>
/// <param name="Market">市場。</param>
/// <param name="Totals">区分別の合計（7 件・enum 宣言順）。</param>
public sealed record PositionExpenseSummary(
    string Symbol,
    Market Market,
    IReadOnlyList<TradeExpenseCategoryTotal> Totals)
{
    /// <summary>指定区分の合計を返す。7 区分は常に存在するため、見つからないのは異常である。</summary>
    public TradeExpenseCategoryTotal For(TradeExpenseCategory category) =>
        Totals.FirstOrDefault(t => t.Category == category)
        ?? throw new InvalidOperationException($"区分 {category} の合計が集計に含まれていない（7 区分は常に存在する）。");

    /// <summary>
    /// **費用の合計**（USD）。
    /// <para>
    /// 🔴 <see cref="TradeExpenseCategory.Realized"/> を**含まない**（実現損益は費用ではない）。
    /// 🔴 <see cref="TradeExpenseCategory.DividendInLieu"/> は**含む**（配当相当額の支払いは費用である）。
    /// </para>
    /// </summary>
    public decimal TotalExpensesUsd =>
        Totals.Where(t => TradeExpenseClassification.IsExpense(t.Category)).Sum(t => t.AmountUsd);

    /// <summary>実現損益の合計（USD・符号付き）。**費用は 1 円も混ざらない。**</summary>
    public decimal RealizedUsd => For(TradeExpenseCategory.Realized).AmountUsd;
}
