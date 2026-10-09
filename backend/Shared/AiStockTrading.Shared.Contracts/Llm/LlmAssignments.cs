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
    // FR-04, FR-15, ADR-0014, ADR-0011, #1295, IADR-0524: 利用者裁定 2026-10-10（planning#783）で全用途を 5.5 系へ切り替えた
    // （opus-5 → opus-5-5・sonnet-5 → sonnet-5-5・haiku-4-5 → haiku-5-5）。取引判断の 2 層も含む。

    /// <summary>取引判断の第 1 候補（ピン留め・ADR-0011 / ADR-0014 §決定1）。日報の第 1 候補と週報・月報の第 2 候補。</summary>
    public const string Sonnet55 = "claude-sonnet-5-5";

    /// <summary>月報・週報の第 1 候補（ADR-0015 / ADR-0014）。</summary>
    public const string Opus55 = "claude-opus-5-5";

    /// <summary>スクリーニング層の割当と、日報の第 2 候補（ADR-0017 決定1）。</summary>
    public const string Haiku55 = "claude-haiku-5-5";

    // ---- 移行期間に限り受ける直前世代（#1295, IADR-0524。外すのは #1296） --------------------------------
    // 🔴 **一時措置である。** 基盤（MSP）の LLM ゲートウェイは**構成したモデル名**を応答に名乗り、本表はそれを完全一致で照合する。
    // AST と MSP のどちらかが先に切り替わると、取引判断が `Unassigned`（Allowed=false）で止まる。そこで MSP の切り替えと
    // PoC の確認が済むまで、各 5.5 系 ID の直前世代を**同じ位置**（第 1 候補・フォールバック先）として受ける。
    // 受けた評価には `LlmAssignmentEvaluation.PreviousGenerationAccepted` の印が付く。
    // 🔴 **Stage 0 の両層の組の判定（`Stage0TwoTierModels`）は直前世代を受けない**（旧組での合格は 5.5 系の組の合格にならない。
    // ADR-0011 / ADR-0014 決定3 / ADR-0054 決定3）。

    /// <summary>直前世代（移行期間のみ受ける）: <see cref="Sonnet55"/> の前。</summary>
    public const string Sonnet5 = "claude-sonnet-5";

    /// <summary>直前世代（移行期間のみ受ける）: <see cref="Opus55"/> の前。</summary>
    public const string Opus5 = "claude-opus-5";

    /// <summary>直前世代（移行期間のみ受ける）: <see cref="Haiku55"/> の前。</summary>
    public const string Haiku45 = "claude-haiku-4-5";

    /// <summary>
    /// 移行期間に限り、5.5 系 ID（キー）と同じ位置で受ける直前世代の ID（値）。#1296 で撤去する。
    /// </summary>
    public static IReadOnlyDictionary<string, string> PreviousGenerationAccepted { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Sonnet55] = Sonnet5,
            [Opus55] = Opus5,
            [Haiku55] = Haiku45,
        };

    /// <summary>
    /// **本システムでは使用しないモデル**（ADR-0015 / ADR-0017 決定1）。ZDR（ゼロデータ保持）非対応であり、
    /// 基盤の `NonZdrModels` に載るモデルである。どの用途の第 1・第 2 候補にも現れてはならない。
    /// </summary>
    public const string ForbiddenModel = "claude-fable-5";

    /// <summary>
    /// <see cref="ForbiddenModel"/> の後継。利用者裁定 2026-10-10（planning#783）で同じ扱い（使用しない）とした（#1295, IADR-0524）。
    /// </summary>
    public const string ForbiddenModelSuccessor = "claude-fable-5-1";

    /// <summary>使用しないモデルの全体（大小無視）。</summary>
    public static IReadOnlySet<string> ForbiddenModels { get; } =
        new HashSet<string>([ForbiddenModel, ForbiddenModelSuccessor], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 用途別の割当（順序つき）。**この並びと値が計画の確定値であり、スナップショットテストで固定する。**
    /// </summary>
    public static IReadOnlyList<LlmAssignment> All { get; } =
    [
        // ADR-0017 決定2: 取引判断はいかなる理由でもフォールバックしない。
        // モデルが利用できない場合、取引判断は実行されず発注も行われない（障害ではなく設計上の正常な結果）。
        new(LlmPurposes.TradeDecision, Sonnet55, [], FallbackAllowed: false),
        // 01_architecture-overview: スクリーニングは軽量モデル。取引判断の一部なのでフォールバックは禁止。
        // 入力がコンテキスト上限に当たったときは**入力を切り詰める**（上位モデルへ退避しない。利用者裁定 2026-08-02）。
        new(LlmPurposes.TradeDecisionScreening, Haiku55, [], FallbackAllowed: false),
        // ADR-0015 §決定（第 1 候補）＋ ADR-0017 決定1（第 2 候補）。
        new(LlmPurposes.ReportMonthly, Opus55, [Sonnet55], FallbackAllowed: true),
        new(LlmPurposes.ReportWeekly, Opus55, [Sonnet55], FallbackAllowed: true),
        new(LlmPurposes.ReportDaily, Sonnet55, [Haiku55], FallbackAllowed: true),
    ];

    /// <summary>用途の割当を引く（未登録は null）。用途キーの大小は無視する。</summary>
    public static LlmAssignment? For(string? purpose) =>
        string.IsNullOrWhiteSpace(purpose)
            ? null
            : All.FirstOrDefault(a => string.Equals(a.Purpose, purpose, StringComparison.OrdinalIgnoreCase));

    /// <summary>禁止モデルか（大小無視）。</summary>
    public static bool IsForbidden(string? model) =>
        model?.Trim() is { Length: > 0 } trimmed && ForbiddenModels.Contains(trimmed);

    /// <summary>
    /// 移行期間に限り <paramref name="assignedModel"/>（5.5 系 ID）と同じ位置で受ける直前世代か（大小無視）。#1295 / #1296。
    /// </summary>
    public static bool IsPreviousGenerationOf(string assignedModel, string? model) =>
        PreviousGenerationAccepted.TryGetValue(assignedModel, out var previous)
        && string.Equals(model, previous, StringComparison.OrdinalIgnoreCase);
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

        // #1295, IADR-0524（移行期間のみ・#1296 で撤去）: 直前世代は 5.5 系 ID と同じ位置として受け、印を付ける。
        // 位置は変えない —— 第 1 候補の直前世代は Primary、フォールバック先の直前世代は FallbackFired（取引判断系は
        // 鎖が空なのでフォールバック先の直前世代も存在しない＝フォールバック禁止の意味は変わらない）。
        if (LlmAssignments.IsPreviousGenerationOf(assignment.PrimaryModel, model))
            return new LlmAssignmentEvaluation(LlmAssignmentOutcome.Primary, assignment.PrimaryModel, model, Allowed: true)
            {
                PreviousGenerationAccepted = true,
            };

        if (assignment.FallbackModels.Any(m => LlmAssignments.IsPreviousGenerationOf(m, model)))
            return new LlmAssignmentEvaluation(
                LlmAssignmentOutcome.FallbackFired, assignment.PrimaryModel, model, assignment.FallbackAllowed)
            {
                PreviousGenerationAccepted = true,
            };

        return new LlmAssignmentEvaluation(LlmAssignmentOutcome.Unassigned, assignment.PrimaryModel, model, Allowed: false);
    }
}

// 評価結果。ExpectedModel は用途の第 1 候補（用途未登録なら null）、EffectiveModel は基盤が名乗った値。
public readonly record struct LlmAssignmentEvaluation(
    LlmAssignmentOutcome Outcome,
    string? ExpectedModel,
    string? EffectiveModel,
    bool Allowed)
{
    /// <summary>
    /// #1295, IADR-0524（移行期間のみ・#1296 で撤去）: 実効モデルが 5.5 系 ID ではなく、その直前世代として受けたものか。
    /// </summary>
    public bool PreviousGenerationAccepted { get; init; }

    /// <summary>
    /// 第 1 候補（ピン）の**現行 ID そのもの**に答えられたか（直前世代の受け入れを含めない）。
    /// Stage 0 の両層の組の判定（ADR-0011 / ADR-0014 決定3 / ADR-0054 決定3）はこちらを使う。
    /// </summary>
    public bool MatchesCurrentPin => Outcome == LlmAssignmentOutcome.Primary && !PreviousGenerationAccepted;
}
