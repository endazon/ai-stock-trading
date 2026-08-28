namespace AiStockTrading.Shared.Contracts.Trading;

/// <summary>
/// FR-11, ADR-0016 決定15, #339: 取引記録の**経費区分**。
/// <para>
/// 計画（FR-11・ADR-0016 決定15）が名指しした 7 区分そのものであり、**この 7 つが全部である**。
/// 集計機能（FR-18）は対象外のままだが、**集計は後から作れても記録は遡って復元できない**ため、
/// 記録の側に区分を最初から持たせる。
/// </para>
/// <para>
/// 🔴 <b>本 enum に「受け取った配当」を表す区分は無い。</b>
/// <see cref="DividendInLieu"/> は空売り建玉の保有者が**支払う**配当相当額であり、
/// 税務上は譲渡費用に近い扱いである。**配当（収入）と同じ区分に置くと後から区別できない。**
/// </para>
/// <para>
/// 🔴 <b>序数を書き換えない。</b> 区分は永続（監査台帳の JSON）と HTTP 経路で往来し得るため、
/// 既存メンバの間へ挿入すると**過去に記録した経費の意味が変わる**。追加は常に末尾へ行う
/// （<c>RejectionReason</c> と同じ規律。<c>TradeExpenseCategoryTests</c> が全メンバの序数を表で固定する）。
/// </para>
/// </summary>
public enum TradeExpenseCategory
{
    /// <summary>実現損益。**費用ではない**（唯一の損益区分）。損失は負の値で持つ。</summary>
    Realized = 0,

    /// <summary>借株料。日次の計上額は借株料台帳が持ち、その計上が本区分の 1 行になる（ADR-0027 決定1）。</summary>
    BorrowFee = 1,

    /// <summary>信用金利。</summary>
    MarginInterest = 2,

    /// <summary>
    /// **配当相当額の支払い。** 空売り建玉の保有者が権利確定日をまたいだときに支払う。
    /// 🔴 <b>配当の受取ではない。</b> 税務上は譲渡費用に近い扱いであり、
    /// **記録時に分けないと後から区別できない**（ADR-0016 決定15 が要点として名指しした点である）。
    /// </summary>
    DividendInLieu = 3,

    /// <summary>売買手数料。</summary>
    Commission = 4,

    /// <summary>手数料以外の諸費用（規制費用・取引所費用など）。</summary>
    Fee = 5,

    /// <summary>為替コスト（スプレッド・両替費用）。</summary>
    FxCost = 6,
}

/// <summary>
/// FR-11, ADR-0016 決定15, #339: 経費区分の**性質**。
/// <para>
/// 「配当相当額の支払いを配当と混同しない」という要件は、値の検査ではなく**この写像**で満たす ——
/// <see cref="TradeExpenseCategory.DividendInLieu"/> は <see cref="TransferCost"/> であり、
/// 収入側の性質は本 enum に存在しない。
/// </para>
/// </summary>
public enum TradeExpenseNature
{
    /// <summary>実現損益（符号付き）。費用合計には含めない。</summary>
    RealizedProfitAndLoss = 0,

    /// <summary>譲渡費用に近い扱いの費用（正の値で費用額を持つ）。</summary>
    TransferCost = 1,
}
