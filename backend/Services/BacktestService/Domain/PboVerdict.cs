using System.Globalization;

namespace BacktestService.Domain;

// FR-15, FR-20, ADR-0008, ADR-0039 決定1, #777, IADR-0337 決定1: 過剰適合確率（PBO）の判定結果。
//
// 🔴 **「測っていない」を 0 で表せる口を型から消すための型である。**
// 計画 ADR-0039 は「**`評価不能` は『合格』ではない。判定結果として明示的に記録し、合否の根拠から外す。
// 「PBO は 0 だった」と書かない**」と定めた —— 測っていないことと差が無かったことを読み分けられる形にする。
//
// PBO が測るのは「多数試したうちの最良を選んだこと」による過剰適合であり、**選択が無いところに罰する対象が
// 無い**。これは較正の数値ではなく指標の定義から出る（ADR-0039 実測 2-b）。
public abstract record PboVerdict
{
    // 閉じた階層にする（第 3 の状態を外部から生やせない）。入れ子型だけが private コンストラクタへ到達できる。
    private PboVerdict()
    {
    }

    /// <summary>
    /// FR-15, ADR-0008: 探索があり、PBO を実際に算出した。<paramref name="Value"/> は CSCV の推定値である。
    /// </summary>
    public sealed record Evaluated(double Value) : PboVerdict;

    /// <summary>
    /// FR-15, ADR-0039 決定1: PBO を**測っていない**。<paramref name="Reason"/> がなぜ測らなかったかを運ぶ。
    /// 🔴 これは「合格」ではない —— 判定器は本状態のとき PBO 条件を**合否の根拠から外す**（満たしたと数えない）。
    /// </summary>
    public sealed record NotEvaluable(PboNotEvaluableReason Reason) : PboVerdict;

    /// <summary>PBO を実際に算出したか。<c>false</c> なら合否の根拠から外れている（合格したのではない）。</summary>
    public bool IsEvaluated => this is Evaluated;

    /// <summary>
    /// 台帳・ログの表示用の文字列。🔴 **評価不能のとき数値を返さない**（ADR-0039 決定1「『PBO は 0』と書かない」）。
    /// 表示の単一情報源であり、面ごとに書式がドリフトするのを防ぐ。
    /// </summary>
    public string Format() => this switch
    {
        Evaluated e => e.Value.ToString("F2", CultureInfo.InvariantCulture),
        NotEvaluable n => $"評価不能({n.Reason})",
        _ => throw new InvalidOperationException($"未知の PBO 判定結果です: {GetType().Name}"),
    };
}

// FR-15, ADR-0039 決定1, #777, IADR-0337 決定1: PBO を評価しなかった理由。
//
// 🔴 **2 つを分けているのは、「探索が無いので測れない」と「そもそも判定していない」が別の事実だからである。**
// 前者は本番戦略の構造的な性質（合否は他の 6 条件で決まる）、後者は駆動側の事前条件による不合格固定である。
public enum PboNotEvaluableReason
{
    /// <summary>
    /// FR-15, ADR-0039 決定1: **探索を持たない**（試行 1 本）。記録再生戦略はパラメータ探索を持たないため、
    /// PBO が測る対象（多数試したうちの最良を選んだこと）が存在しない。合否は他の指標で決める。
    /// </summary>
    NoSearchSingleTrial,

    /// <summary>
    /// FR-15, IADR-0310, IADR-0318: **判定そのものを走らせていない**（過去データが空・記録が無い／構成と
    /// 不整合・標本不足）。算出していないのだから値を名乗らない。
    /// </summary>
    NotEvaluated,
}
