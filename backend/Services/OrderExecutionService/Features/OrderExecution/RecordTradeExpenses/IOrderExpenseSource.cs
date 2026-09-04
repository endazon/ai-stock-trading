using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution.RecordTradeExpenses;

// FR-11, FR-16, UC-07, ADR-0016 決定15, ADR-0027 決定4, #633, IADR-0300:
// **約定 1 件に対して発生した経費明細の取得ポート**（実費の供給口）。
//
// ADR-0016 決定15 は取引記録へ経費区分 7 種を持たせ、**建玉単位で紐づけられること**を要件とした。
// 区分・集計・イベント・監査の受け皿は IADR-0226 で既に置かれているが、**供給は意図的に遮断されていた**
// （同決定7）。本ポートはその遮断を「実装が無い」から「取得できないと分かっている」へ変える。
//
// 🔴 **実費の取得そのもの（moomoo の OnReply_GetOrderFee）は本ポートの実装であって、本ポートではない。**
// 段 2（実口座での応答仕様の確認が前提）で別途実装する。段 1 の既定は
// <see cref="OrderExecutionService.Infrastructure.ExternalServices.UnsuppliedOrderExpenseSource"/>
// （常に取得不能）である。
public interface IOrderExpenseSource
{
    /// <summary>
    /// 約定 1 件の経費明細を照会する。
    /// <para>
    /// 🔴 <b>取得できないときは空の明細を返さない</b>（<see cref="OrderExpenseLookup.Unavailable"/> を返す）。
    /// 空を返すと「照会できて費用が 1 円も無かった」と読め、供給を忘れた期間がそのまま
    /// 「費用なし」で通る（ADR-0027 決定4 が借株料について塞いだのと同じ誤読である）。
    /// </para>
    /// </summary>
    Task<OrderExpenseLookup> GetOrderExpensesAsync(
        OrderExpenseQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// FR-11, #633: 経費明細の照会の入力（約定 1 件を指す）。
/// <para>
/// <paramref name="Symbol"/> と <paramref name="Market"/> の組が**建玉の一次識別子**である
/// （ADR-0027 決定2）。銘柄別・口座全体は合算で導出するため、ここには持たせない。
/// </para>
/// </summary>
/// <param name="DecisionId">取引判断 1 件との相関。</param>
/// <param name="OrderId">ブローカーの注文 ID。</param>
/// <param name="Symbol">銘柄コード。</param>
/// <param name="Market">市場。</param>
/// <param name="ExecutedAt">約定を観測した時刻（帰属日の決定に用いる）。</param>
public sealed record OrderExpenseQuery(
    Guid DecisionId,
    string OrderId,
    string Symbol,
    Market Market,
    DateTimeOffset ExecutedAt);

/// <summary>
/// FR-11, ADR-0027 決定4, #633, IADR-0300: 経費明細の照会結果。**2 状態しか無い。**
/// <para>
/// 🔴 <b>「取得できなかった」は金額の欄を持たない。</b> 1 つの型へ畳んで明細を null 許容にすると、
/// 未供給を 0 として合計へ混ぜる経路が**型の上で表現可能**になる —— 借株料で
/// <c>BorrowFeeAccrued</c> と <c>BorrowFeeAccrualUnavailable</c> を別の型へ分けたのと同じ規律である
/// （IADR-0183）。<see cref="Lines"/> は供給されたときにしか読めない。
/// </para>
/// </summary>
public sealed class OrderExpenseLookup
{
    private readonly IReadOnlyList<TradeExpense>? _lines;
    private readonly string? _reason;

    private OrderExpenseLookup(IReadOnlyList<TradeExpense>? lines, string? reason)
    {
        _lines = lines;
        _reason = reason;
    }

    /// <summary>
    /// **照会できた**（<paramref name="lines"/> が空なら「照会できて 1 件も無かった」）。
    /// 🔴 取得できなかった場合にこれを使わない。
    /// </summary>
    public static OrderExpenseLookup Supplied(IReadOnlyList<TradeExpense> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return new OrderExpenseLookup(lines, null);
    }

    /// <summary>**照会できなかった。** 理由は診断用であり、金額は持たない。</summary>
    public static OrderExpenseLookup Unavailable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new OrderExpenseLookup(null, reason);
    }

    /// <summary>照会できたか。</summary>
    public bool IsSupplied => _lines is not null;

    /// <summary>
    /// 明細。**照会できなかった場合は例外で落ちる**（0 件として合計へ混ぜられないようにするため）。
    /// </summary>
    /// <exception cref="InvalidOperationException">照会できていない結果から明細を読んだ。</exception>
    public IReadOnlyList<TradeExpense> Lines =>
        _lines ?? throw new InvalidOperationException(
            "経費明細を照会できていない結果から明細を読み出した（未供給は 0 件ではない）。");

    /// <summary>取得できなかった理由。**照会できた場合は例外で落ちる。**</summary>
    /// <exception cref="InvalidOperationException">照会できた結果から理由を読んだ。</exception>
    public string UnavailableReason =>
        _reason ?? throw new InvalidOperationException(
            "照会できた結果には取得できなかった理由が無い。");
}
