using System.Globalization;
using AiStockTrading.Shared.Contracts.Backtest;

namespace BacktestService.Domain;

// FR-04, FR-15, FR-20, ADR-0036 決定1, #749, IADR-0387: **判定母集団から外した判断の集計**。
//
// 計画 ADR-0036 決定1 は「**外した範囲は記録に残す** —— 『何を外したか』が分からないと、合格が何についての
// 合格なのかが読めない」と定めた。本型はその集計を verdict（`Stage0Decision`）へ載せる形である。
//
// 🔴 **「除外 0 件」と「件数が分からない」を同じ値で表せる口を型から消す。** 記録が再構成可否を申告して
// いなければ、除外すべき判断があったかどうかそのものが読めない —— そこへ 0 を置くと「痩せた入力は 1 件も
// 無かった」と読める（`PboVerdict` が「測っていない」を 0 で表せる口を消したのと同型。ADR-0039 / IADR-0337）。
public abstract record Stage0ExclusionSummary
{
    // 閉じた階層にする（第 3 の状態を外部から生やせない）。入れ子型だけが private コンストラクタへ到達できる。
    private Stage0ExclusionSummary()
    {
    }

    /// <summary>
    /// FR-15, ADR-0036 決定1: 記録が再構成可否を申告しており、**件数を数えた**。
    /// <paramref name="Excluded"/> が 0 なら「痩せた入力に依存する判断は 1 件も無かった」という**実測**である。
    /// </summary>
    /// <param name="Excluded">再構成不可に依存するため判定母集団から外した判断の件数。</param>
    /// <param name="Evaluated">判定母集団に残った判断の件数（**合格が何についての合格かを読む分母**）。</param>
    /// <param name="Kinds">外す理由になった入力の種別（安定順・除外 0 件なら空）。</param>
    public sealed record Counted(
        int Excluded, int Evaluated, IReadOnlyList<Stage0AsOfInputKind> Kinds) : Stage0ExclusionSummary;

    /// <summary>
    /// FR-15, ADR-0036 決定1: 件数が**分からない**。🔴 これは「除外 0 件」ではない。
    /// </summary>
    public sealed record Unknown(Stage0ExclusionUnknownReason Reason) : Stage0ExclusionSummary;

    /// <summary>件数を数えたか。<c>false</c> なら件数を名乗らない（0 件だったのではない）。</summary>
    public bool IsCounted => this is Counted;

    /// <summary>
    /// 台帳・ログの表示用の文字列。🔴 **分からないときに数値を返さない**（0 件と未供給を読み分ける）。
    /// 表示の単一情報源であり、面ごとに書式がドリフトするのを防ぐ。
    /// </summary>
    public string Format() => this switch
    {
        Counted { Excluded: 0 } c =>
            $"除外なし(母集団 {c.Evaluated.ToString(CultureInfo.InvariantCulture)} 件)",
        Counted c =>
            $"除外 {c.Excluded.ToString(CultureInfo.InvariantCulture)} 件"
            + $"/母集団 {c.Evaluated.ToString(CultureInfo.InvariantCulture)} 件({string.Join("・", c.Kinds)})",
        Unknown u => $"不明({u.Reason})",
        _ => throw new InvalidOperationException($"未知の除外集計です: {GetType().Name}"),
    };
}

// FR-15, ADR-0036 決定1, #749, IADR-0387: 除外件数が分からない理由。
//
// 🔴 **2 つを分けているのは、「記録が申告していない」と「判定を走らせていない」が別の事実だからである。**
// 前者は記録の質の問題（申告のない記録を判定へ通さない）、後者は駆動側の事前条件による不合格固定である。
public enum Stage0ExclusionUnknownReason
{
    /// <summary>
    /// FR-15, ADR-0036 決定1: **記録が as-of 入力の再構成可否を申告していない**（未申告・部分申告）。
    /// 何を外すべきか読めないため、判定を組ませない（`Stage0GateCheck.InputCompletenessNotDeclared`）。
    /// </summary>
    CompletenessNotDeclared,

    /// <summary>
    /// FR-15, IADR-0310, IADR-0318: **判定そのものを走らせていない**（過去データが空・記録が無い／構成と
    /// 不整合・標本不足）。再生していないのだから件数を名乗らない。
    /// </summary>
    NotEvaluated,
}
