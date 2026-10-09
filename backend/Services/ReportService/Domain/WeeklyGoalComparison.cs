using System.Globalization;

namespace ReportService.Domain;

// FR-06, FR-16, 計画 ADR-0059 決定 2〜4, #1218, IADR-0519 決定 4・5: 週次目標（前週の週報 §6 の書式行）と実績（週報 §1 と同じ定義の実現損益）の照合。
// 🔴 **比較はコードで行い、LLM は評価の文章だけを書く**（04_report-templates §採用方針）。値は丸めずに比べる（表示だけ通貨の補助単位で丸める）。
// 🔴 **達成・未達は判定しない。** 範囲のどこで分けるかを計画が決めていない（計画 ADR-0059 §結果・フォローアップ 6）。位置（下限未満・範囲内・上限超）と差を事実として示す。

/// <summary>
/// 参照する週報（計画 ADR-0059 決定 4）。<see cref="SourcePeriodKey"/> が null なら確定済みの週報が 1 件も無い（週次目標なし）。
/// <see cref="ExpectedPeriodKey"/> は前週の週報（当週を対象期間とする目標を持つ週報）の期間キー。両者が違えば、前週の週報が未確定のため最新の確定済みを使った。
/// </summary>
public sealed record WeeklyGoalReference(string ExpectedPeriodKey, string? SourcePeriodKey, WeeklyGoalLineReading? Reading)
{
    /// <summary>確定済みの週報が 1 件も無い。</summary>
    public static WeeklyGoalReference None(string expectedPeriodKey) => new(expectedPeriodKey, null, null);

    /// <summary>前週の週報ではなく、より前の確定済み週報の目標を使ったか。</summary>
    public bool IsFallback => SourcePeriodKey is not null && !string.Equals(SourcePeriodKey, ExpectedPeriodKey, StringComparison.Ordinal);
}

/// <summary>照合の実績（基準通貨建ての実現損益）。<see cref="Amount"/> が null なら算出不能で、理由は <see cref="NotComputableReason"/>。</summary>
public sealed record WeeklyGoalActual(decimal? Amount, string? NotComputableReason)
{
    public static WeeklyGoalActual Of(decimal amount) => new(amount, null);

    public static WeeklyGoalActual NotComputable(string reason) => new(null, reason);

    /// <summary>
    /// 週報 §1 と同じ規則で、損益の集計から実績を取り出す。部分値（算定できない決済・期間開始時点の在庫の照会失敗）は数字にしない
    /// （ReportRenderer の「算出不能」と同じ理由。#892・#1181）。
    /// </summary>
    public static WeeklyGoalActual From(PnlSummary pnl)
    {
        ArgumentNullException.ThrowIfNull(pnl);
        if (pnl.UnvaluedSettlementCount > 0)
        {
            return NotComputable(string.Format(CultureInfo.InvariantCulture,
                "期間より前に建てた建玉の決済が {0} 件あり、その取得原価が分かりません", pnl.UnvaluedSettlementCount));
        }

        return pnl.OpeningInventoryUnknown
            ? NotComputable("期間開始時点の在庫を照会できず、持ち越した建玉の取得原価が分かりません")
            : Of(pnl.RealizedPnlNet);
    }
}

/// <summary>照合の結果の種類。</summary>
public enum WeeklyGoalOutcome
{
    /// <summary>確定済みの週報が 1 件も無い（週次目標なし）。</summary>
    NoGoal,

    /// <summary>参照する週報に書式どおりの行が無い・書式外・単位が基準通貨でない（照合不能）。</summary>
    NotComparable,

    /// <summary>目標はあるが実績が算出不能。</summary>
    NotComputable,

    /// <summary>照合した。</summary>
    Compared,
}

/// <summary>実績の範囲に対する位置。</summary>
public enum WeeklyGoalPosition
{
    BelowRange,
    WithinRange,
    AboveRange,
}

/// <summary>週次目標の照合の結果（純関数 <see cref="Evaluate"/> が作る）。</summary>
public sealed record WeeklyGoalComparison(
    WeeklyGoalOutcome Outcome,
    WeeklyGoalReference Reference,
    WeeklyGoalActual Actual,
    WeeklyGoalPosition? Position)
{
    /// <summary>
    /// 照合する。範囲の両端はちょうどなら範囲内（下限 ≤ 実績 ≤ 上限）。参照値が使えないときは実績を見ない（照合不能・週次目標なしを優先する）。
    /// </summary>
    public static WeeklyGoalComparison Evaluate(WeeklyGoalReference reference, WeeklyGoalActual actual)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(actual);

        if (reference.SourcePeriodKey is null || reference.Reading is null)
            return new(WeeklyGoalOutcome.NoGoal, reference, actual, null);

        if (!reference.Reading.IsConforming)
            return new(WeeklyGoalOutcome.NotComparable, reference, actual, null);

        if (actual.Amount is not { } amount)
            return new(WeeklyGoalOutcome.NotComputable, reference, actual, null);

        var lower = reference.Reading.Lower!.Value;
        var upper = reference.Reading.Upper!.Value;
        var position = amount < lower
            ? WeeklyGoalPosition.BelowRange
            : amount > upper ? WeeklyGoalPosition.AboveRange : WeeklyGoalPosition.WithinRange;
        return new(WeeklyGoalOutcome.Compared, reference, actual, position);
    }

    /// <summary>範囲の外にいるときの、近いほうの端との差（実績 − 端。範囲内は null）。</summary>
    public decimal? DistanceOutside => Position switch
    {
        WeeklyGoalPosition.BelowRange => Actual.Amount!.Value - Reference.Reading!.Lower!.Value,
        WeeklyGoalPosition.AboveRange => Actual.Amount!.Value - Reference.Reading!.Upper!.Value,
        _ => null,
    };

    /// <summary>参照値の表記（例「-200.00 USD 〜 +500.00 USD（weekly-2026-W40 の §6 の数値目標）」）。照合不能・目標なしは null。</summary>
    public string? GoalText => Outcome is WeeklyGoalOutcome.Compared or WeeklyGoalOutcome.NotComputable
        ? string.Format(CultureInfo.InvariantCulture, "{0} 〜 {1}（{2} の §6 の数値目標）",
            ReportAmountFormat.Base(Reference.Reading!.Lower!.Value), ReportAmountFormat.Base(Reference.Reading.Upper!.Value),
            Reference.SourcePeriodKey)
        : null;

    /// <summary>位置の表記（照合したときだけ）。例「範囲内」「下限を下回る（下限との差 -12.50 USD）」。</summary>
    public string? PositionText => Position switch
    {
        WeeklyGoalPosition.WithinRange => "範囲内",
        WeeklyGoalPosition.BelowRange => $"下限を下回る（下限との差 {ReportAmountFormat.Base(DistanceOutside!.Value)}）",
        WeeklyGoalPosition.AboveRange => $"上限を上回る（上限との差 {ReportAmountFormat.Base(DistanceOutside!.Value)}）",
        _ => null,
    };

    /// <summary>照合不能・週次目標なしの理由（照合したとき・算出不能のときは null）。</summary>
    public string? UnavailableText => Outcome switch
    {
        WeeklyGoalOutcome.NoGoal => "**週次目標なし**（確定済みの週報がありません）",
        WeeklyGoalOutcome.NotComparable => Reference.Reading!.Status switch
        {
            WeeklyGoalLineStatus.UnitMismatch => string.Format(CultureInfo.InvariantCulture,
                "**照合不能**（{0} の §6 の数値目標の単位が基準通貨 USD ではありません〔{1}〕。換算しません）",
                Reference.SourcePeriodKey, Reference.Reading.Unit),
            WeeklyGoalLineStatus.Missing => string.Format(CultureInfo.InvariantCulture,
                "**照合不能**（週次目標が書式どおりでない: {0} の方針に「数値目標:」行がありません）", Reference.SourcePeriodKey),
            _ => string.Format(CultureInfo.InvariantCulture,
                "**照合不能**（週次目標が書式どおりでない: {0} の方針の「数値目標」の行が書式に合わないか、2 行以上あります）", Reference.SourcePeriodKey),
        },
        _ => null,
    };

    /// <summary>前週の週報が未確定のため、より前の週報の目標を使ったときの注記（それ以外は null）。</summary>
    public string? FallbackNote => Reference.IsFallback
        ? string.Format(CultureInfo.InvariantCulture,
            "前週の週報（{0}）が確定していないため、最新の確定済み週報 {1} の目標と照らしています。", Reference.ExpectedPeriodKey, Reference.SourcePeriodKey)
        : null;
}
