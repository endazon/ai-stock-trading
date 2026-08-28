namespace AiStockTrading.Shared.Contracts.Llm;

// FR-04, FR-06, ADR-0014, ADR-0015, ADR-0017, #335, IADR-0215:
// 用途別の割当モデルとフォールバック順序の表。**本システムが期待する値**の単一情報源である。
//
// 実際にモデルを選ぶのは基盤（microservices-platform）の LlmRouter であり、本表はそれを置き換えない。
// 本表の役割は**基盤が返した実効モデルを検証すること**にある —— 基盤で用途エントリが未登録・
// ZDR 除外・提供終了のいずれかが起きると、`LlmRouter` は例外もログも出さずに `DefaultModel` へ落ちる
// （platform IADR-0102）。落ちたことに気づく仕組みが呼び出し側に無ければ、
// ADR-0014 §決定3 の「検証したモデルと本番モデルの一致」は成立しない。
//
// 値の出典（計画・確定値）:
//   ADR-0014 §決定1 ＋ 2026-08-01 改訂表 / ADR-0015 §決定（月報）/ ADR-0017 決定1・決定2
//   01_architecture-overview §判断の二段化（スクリーニング層の割当）
public static class LlmAssignments
{
    /// <summary>取引判断の第 1 候補（ピン留め・ADR-0011 / ADR-0014 §決定1）。</summary>
    public const string Sonnet5 = "claude-sonnet-5";

    /// <summary>月報・週報の第 1 候補（ADR-0015 / ADR-0014）。</summary>
    public const string Opus5 = "claude-opus-5";

    /// <summary>スクリーニング層の割当と、日報の第 2 候補（ADR-0017 決定1）。</summary>
    public const string Haiku45 = "claude-haiku-4-5";

    /// <summary>
    /// **本システムでは使用しないモデル**（ADR-0015 / ADR-0017 決定1）。ZDR（ゼロデータ保持）非対応であり、
    /// 基盤の `NonZdrModels` に載る唯一のモデルである。どの用途の第 1・第 2 候補にも現れてはならない。
    /// </summary>
    public const string ForbiddenModel = "claude-fable-5";

    /// <summary>
    /// 用途別の割当（順序つき）。**この並びと値が計画の確定値であり、スナップショットテストで固定する。**
    /// </summary>
    public static IReadOnlyList<LlmAssignment> All { get; } =
    [
        // ADR-0017 決定2: 取引判断はいかなる理由でもフォールバックしない。
        // モデルが利用できない場合、取引判断は実行されず発注も行われない（障害ではなく設計上の正常な結果）。
        new(LlmPurposes.TradeDecision, Sonnet5, [], FallbackAllowed: false),
        // 01_architecture-overview: スクリーニングは軽量モデル。取引判断の一部なのでフォールバックは禁止。
        // 入力がコンテキスト上限に当たったときは**入力を切り詰める**（上位モデルへ退避しない。利用者裁定 2026-08-02）。
        new(LlmPurposes.TradeDecisionScreening, Haiku45, [], FallbackAllowed: false),
        // ADR-0015 §決定（第 1 候補）＋ ADR-0017 決定1（第 2 候補）。
        new(LlmPurposes.ReportMonthly, Opus5, [Sonnet5], FallbackAllowed: true),
        new(LlmPurposes.ReportWeekly, Opus5, [Sonnet5], FallbackAllowed: true),
        new(LlmPurposes.ReportDaily, Sonnet5, [Haiku45], FallbackAllowed: true),
    ];

    /// <summary>用途の割当を引く（未登録は null）。用途キーの大小は無視する。</summary>
    public static LlmAssignment? For(string? purpose) =>
        string.IsNullOrWhiteSpace(purpose)
            ? null
            : All.FirstOrDefault(a => string.Equals(a.Purpose, purpose, StringComparison.OrdinalIgnoreCase));

    /// <summary>禁止モデルか（大小無視）。</summary>
    public static bool IsForbidden(string? model) =>
        string.Equals(model?.Trim(), ForbiddenModel, StringComparison.OrdinalIgnoreCase);
}

// 1 用途分の割当。FallbackModels は**第 1 候補より後ろ**だけを順序どおりに持つ（空＝鎖なし）。
// FallbackAllowed=false は「鎖がたまたま空」ではなく「**フォールバックしてはならない**」という統制上の宣言である
// （ADR-0017 決定2）。両者を型で区別しないと、後から鎖を足す変更が統制の逸脱だと気づけない。
public sealed record LlmAssignment(
    string Purpose,
    string PrimaryModel,
    IReadOnlyList<string> FallbackModels,
    bool FallbackAllowed);

// 実効モデルを割当表と突き合わせた結果。
public enum LlmAssignmentOutcome
{
    /// <summary>第 1 候補（ピン）どおり。</summary>
    Primary,

    /// <summary>第 2 候補以降が使われた＝フォールバックが発火した。</summary>
    FallbackFired,

    /// <summary>表のどこにも無いモデル（用途未登録・基盤の DefaultModel へ落ちた等）。</summary>
    Unassigned,

    /// <summary>本システムで使用しないと決めたモデル（ADR-0015 / ADR-0017 決定1）。</summary>
    Forbidden,
}

// FR-04, ADR-0017, #335, IADR-0215/0216: 実効モデルの評価（純関数）。
public static class LlmAssignmentEvaluator
{
    /// <summary>
    /// 用途と**基盤が実際に使ったモデル**から評価結果を返す。
    /// <para>
    /// `Allowed` は「その応答を成果物として採用してよいか」である。取引判断系では第 1 候補のみ真であり、
    /// フォールバック先・未割当・禁止モデルはすべて偽になる（ADR-0017 決定2）。報告書では第 2 候補も真である。
    /// </para>
    /// </summary>
    public static LlmAssignmentEvaluation Evaluate(string? purpose, string? effectiveModel)
    {
        var assignment = LlmAssignments.For(purpose);
        var model = effectiveModel?.Trim();

        // 禁止モデルは用途によらず常に不可（表に載っていないので Unassigned にもなるが、
        // 「未知だった」と「使わないと決めていた」は別の事実なので区別して記録する）。
        if (LlmAssignments.IsForbidden(model))
            return new LlmAssignmentEvaluation(LlmAssignmentOutcome.Forbidden, assignment?.PrimaryModel, model, Allowed: false);

        if (assignment is null)
            return new LlmAssignmentEvaluation(LlmAssignmentOutcome.Unassigned, ExpectedModel: null, model, Allowed: false);

        if (string.Equals(model, assignment.PrimaryModel, StringComparison.OrdinalIgnoreCase))
            return new LlmAssignmentEvaluation(LlmAssignmentOutcome.Primary, assignment.PrimaryModel, model, Allowed: true);

        if (assignment.FallbackModels.Any(m => string.Equals(model, m, StringComparison.OrdinalIgnoreCase)))
            return new LlmAssignmentEvaluation(
                LlmAssignmentOutcome.FallbackFired, assignment.PrimaryModel, model, assignment.FallbackAllowed);

        return new LlmAssignmentEvaluation(LlmAssignmentOutcome.Unassigned, assignment.PrimaryModel, model, Allowed: false);
    }
}

// 評価結果。ExpectedModel は用途の第 1 候補（用途未登録なら null）、EffectiveModel は基盤が名乗った値。
public readonly record struct LlmAssignmentEvaluation(
    LlmAssignmentOutcome Outcome,
    string? ExpectedModel,
    string? EffectiveModel,
    bool Allowed);
