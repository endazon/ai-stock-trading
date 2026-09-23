namespace AiStockTrading.Shared.Contracts.Backtest;

// FR-04, FR-15, ADR-0036 決定1, #749, IADR-0387: **as-of 入力の再構成可否**の契約。
//
// 計画 ADR-0036 決定1（2026-09-09 のオーナー裁定）は「**過去時点の情報が復元できない項目があれば、
// その項目に依存する判断を Stage 0 の合否から外す**」「**外した範囲は記録に残す** —— 『何を外したか』が
// 分からないと、合格が何についての合格なのかが読めない」と定めた。本ファイルはその記録の契約である。
//
// 🔴 **「入力が無かった」と「入力を再構成できなかった」は別の事実である。** 前者はその時点の事実
// （当日ニュースが無かった）であり判断の根拠として正しい。後者は**当時の値が不明**であり、その入力で
// 下した判断は本番の AI 判断とは別のものを測っている（ADR-0036 決定1 の理由＝ADR-0033 決定3 と同じ）。
// **同じ値で表せる口を型から消す**のが本ファイルの目的である（`PboVerdict` が「測っていない」を 0 で
// 表せる口を消したのと同型。ADR-0039 / IADR-0337）。

/// <summary>
/// FR-15, ADR-0036 決定1: 再構成可否を申告する as-of 入力の種別。
/// <para>
/// 🔴 **3 種は ADR-0036 決定1 の (b)(c)(d) と 1 対 1 である。** 同決定は記号を環流 planning#578 の並びから
/// 引いており、**(a) 過去日の終値は対象に入らない**（環流が「情報源が過去分を提供できるか未確認」と挙げた
/// のは (b)(c)(d) の 3 つだけである）。**種別を足すには ADR-0036 の改定が要る。**
/// </para>
/// </summary>
public enum Stage0AsOfInputKind
{
    /// <summary>(b) ニュース・開示（**発行時刻付き**。発行時刻が不明なものは構造的に除外される）。</summary>
    NewsAndDisclosures,

    /// <summary>(c) 当時の確定日報方針。</summary>
    DailyPolicy,

    /// <summary>(d) 非基準通貨市場のその時点の為替レート（基準通貨の市場では定義から 1）。</summary>
    FxRateToBase,
}

/// <summary>
/// FR-15, ADR-0036 決定1: 1 つの as-of 入力が判断時点でどう揃っていたか。
/// </summary>
public enum Stage0AsOfInputAvailability
{
    /// <summary>当時の値を再構成できた。</summary>
    Reconstructed,

    /// <summary>
    /// 🔴 **その時点に存在しなかった**（当日ニュースが無かった等）。**欠如そのものが当時の事実**であり、
    /// 本番の AI 判断も同じ入力で動く。**除外の理由にならない。**
    /// </summary>
    AbsentAtAsOf,

    /// <summary>
    /// 🔴 **当時の値を再構成できなかった**（情報源が過去分を提供しない・発行時刻が不明で時点に置けない）。
    /// **本状態に依存する判断は Stage 0 の判定母集団から除く**（ADR-0036 決定1）。
    /// </summary>
    NotReconstructable,
}

/// <summary>
/// FR-15, ADR-0036 決定1: 入力 1 種の申告。<paramref name="Reason"/> は
/// <see cref="Stage0AsOfInputAvailability.NotReconstructable"/> のときに**何が再構成できなかったか**を
/// 人が読める形で残す（空でもよいが、書けるなら書く —— 環流〔ADR-0036 フォローアップ 5〕の一次情報になる）。
/// </summary>
public sealed record Stage0AsOfInputStatus(
    Stage0AsOfInputKind Kind,
    Stage0AsOfInputAvailability Availability,
    string Reason = "");

/// <summary>
/// FR-15, ADR-0036 決定1, #749, IADR-0387: 申告の読み取り（記録側・再生側で**同じ述語を共有する**純関数）。
/// </summary>
public static class Stage0AsOfInputs
{
    /// <summary>
    /// 申告が覆っていなければならない種別（ADR-0036 決定1 の (b)(c)(d)）。
    /// </summary>
    public static IReadOnlyList<Stage0AsOfInputKind> RequiredKinds { get; } =
    [
        Stage0AsOfInputKind.NewsAndDisclosures,
        Stage0AsOfInputKind.DailyPolicy,
        Stage0AsOfInputKind.FxRateToBase,
    ];

    /// <summary>
    /// 申告として成立しているか。🔴 **3 種すべてを覆っていなければ「未申告」として扱う。**
    /// <para>
    /// 部分申告を「申告した」と数えると、**抜けた種別が黙って「再構成できた」側へ倒れる** ——
    /// 記録が何も言っていない種別を充足と読むのは、ADR-0036 決定1 が禁じた「痩せた入力での結果を
    /// 合格根拠にする」ことそのものである。
    /// </para>
    /// </summary>
    public static bool IsDeclared(IReadOnlyList<Stage0AsOfInputStatus>? statuses) =>
        statuses is not null && RequiredKinds.All(kind => statuses.Any(s => s.Kind == kind));

    /// <summary>
    /// 再構成できなかった種別（安定順）。<c>未申告</c>のときは空を返す ——
    /// **「無い」ではなく「読めない」であり、呼び出し元は <see cref="IsDeclared"/> を先に見ること。**
    /// </summary>
    public static IReadOnlyList<Stage0AsOfInputKind> NotReconstructableKinds(
        IReadOnlyList<Stage0AsOfInputStatus>? statuses) =>
        !IsDeclared(statuses)
            ? []
            : [.. RequiredKinds.Where(kind => statuses!.Any(
                s => s.Kind == kind && s.Availability == Stage0AsOfInputAvailability.NotReconstructable))];

    /// <summary>
    /// 判定母集団から**除くべき**判断か（申告が成立していて、再構成できなかった種別が 1 つ以上ある）。
    /// 🔴 **未申告は本メソッドで false になる。** 未申告は「除外対象ではない」のではなく
    /// **判定そのものを組めない**状態であり、遮断は呼び出し元（`Stage0ReplayEvaluation`）が行う。
    /// </summary>
    public static bool IsExcluded(IReadOnlyList<Stage0AsOfInputStatus>? statuses) =>
        NotReconstructableKinds(statuses).Count > 0;
}
