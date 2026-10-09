using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AiStockTrading.Shared.Contracts.Trading;

namespace ReportService.Domain;

// FR-06, FR-16, 計画 ADR-0059 決定 1・2, #1218, IADR-0519 決定 1: 週報 §6 の**週次目標の書式行**（「数値目標:」行）の文法（純関数・決定的）。
//
// 🔴 **文法の正は本 IADR（IADR-0519）である。** 計画は要件（範囲と単位を書いた決まった書式の 1 行・単位は必須）だけを定め、文法を写さない
// （計画 ADR-0059 決定 1。ADR-0051 決定 1 と同じ形）。
// 🔴 **自由文からは何も読まない**（IADR-0252・IADR-0470 再監査の是正と同じ）。「数値目標」の語を含む行が 1 行だけあり、それが書式どおりのときだけ読む。
//
// 書式（NFKC で全角を半角へ寄せ、前後の空白を除いてから 1 行ずつ）:
//   [- * ・ のどれか 1 つ]数値目標: <下限> 〜 <上限> <単位>
//   - 金額: 符号は任意（+ / -）。整数部は 3 桁ごとのカンマか、カンマなしの 1〜12 桁。小数は 2 桁まで。
//   - 範囲の区切り: 〜（U+301C）か ~（全角の ～ は NFKC で ~ になる）。前後の空白は任意。
//   - 単位: 英大文字 3 字の通貨コードか「円」。必須。基準通貨（MarketCurrency.Base）以外は単位違い（換算しない。計画 ADR-0059 決定 3）。
//   - 下限 ≤ 上限。読んだ語の後ろに文字があれば書式外。
public static class WeeklyGoalLine
{
    /// <summary>行の見出し語（候補の判定にも使う）。</summary>
    public const string Label = "数値目標";

    /// <summary>案内・警告に載せる例（試験が、この例を本文法で読めることを固定する）。</summary>
    public static readonly IReadOnlyList<string> Examples = ["数値目標: -200 〜 +500 USD", "数値目標: 0 〜 1,000 USD"];

    // 🔴 #1218（監査 Y1）: 数字は ASCII の [0-9] に限る。\d は他の文字体系の数字（٣・१२ など。NFKC でも ASCII へ寄らない）にも一致し、
    // decimal.Parse が例外を投げて生成が週の間ずっと落ちる。書式外の行として読まない（Malformed）。
    private const string AmountPattern = @"[+\-]?(?:[0-9]{1,3}(?:,[0-9]{3}){1,3}|[0-9]{1,12})(?:\.[0-9]{1,2})?";

    private static readonly Regex Grammar = new(
        @"\A(?:[\-*・]\s*)?" + Label + @"\s*:\s*(?<lo>" + AmountPattern + @")\s*[〜~]\s*(?<hi>" + AmountPattern + @")\s*(?<unit>[A-Z]{3}|円)\z",
        RegexOptions.CultureInvariant);

    /// <summary>方針の文から週次目標の書式行を読む。<paramref name="policy"/> が null・空なら行なし。</summary>
    public static WeeklyGoalLineReading Parse(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
            return WeeklyGoalLineReading.Missing;

        var candidates = policy
            .Normalize(NormalizationForm.FormKC)
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Contains(Label, StringComparison.Ordinal))
            .ToList();

        if (candidates.Count == 0)
            return WeeklyGoalLineReading.Missing;

        // 2 行以上あれば、どれが目標かを決めない（推測しない）。
        if (candidates.Count > 1)
            return new WeeklyGoalLineReading(WeeklyGoalLineStatus.Malformed, null, null, null, candidates.Count);

        var match = Grammar.Match(candidates[0]);
        if (!match.Success)
            return new WeeklyGoalLineReading(WeeklyGoalLineStatus.Malformed, null, null, null, 1);

        // 文法が ASCII の数字に限るので解釈は失敗しないが、失敗しても例外にせず書式外へ倒す（生成を落とさない）。
        if (ParseAmount(match.Groups["lo"].Value) is not { } lower
            || ParseAmount(match.Groups["hi"].Value) is not { } upper
            || lower > upper)
            return new WeeklyGoalLineReading(WeeklyGoalLineStatus.Malformed, null, null, null, 1);

        var unit = match.Groups["unit"].Value;
        var status = string.Equals(unit, CurrencyFormat.CodeOf(MarketCurrency.Base), StringComparison.Ordinal)
            ? WeeklyGoalLineStatus.Conforming
            : WeeklyGoalLineStatus.UnitMismatch;
        return new WeeklyGoalLineReading(status, lower, upper, unit, 1);
    }

    private static decimal? ParseAmount(string text) =>
        decimal.TryParse(text.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}

/// <summary>週次目標の書式行の読み取りの結果。</summary>
public enum WeeklyGoalLineStatus
{
    /// <summary>書式どおりで、単位が基準通貨。</summary>
    Conforming,

    /// <summary>「数値目標」の語を含む行が無い。</summary>
    Missing,

    /// <summary>候補の行が書式に合わない、または 2 行以上ある。</summary>
    Malformed,

    /// <summary>書式どおりだが、単位が基準通貨でない（換算しない）。</summary>
    UnitMismatch,
}

/// <summary>
/// 週次目標の書式行の読み取り。<see cref="Lower"/>・<see cref="Upper"/> は書式どおりの行（<see cref="WeeklyGoalLineStatus.Conforming"/> と
/// <see cref="WeeklyGoalLineStatus.UnitMismatch"/>）だけが持つ。<see cref="CandidateCount"/> は「数値目標」の語を含む行の数。
/// </summary>
public sealed record WeeklyGoalLineReading(
    WeeklyGoalLineStatus Status, decimal? Lower, decimal? Upper, string? Unit, int CandidateCount)
{
    public static WeeklyGoalLineReading Missing { get; } = new(WeeklyGoalLineStatus.Missing, null, null, null, 0);

    /// <summary>照合に使える（書式どおり・単位が基準通貨）か。</summary>
    public bool IsConforming => Status == WeeklyGoalLineStatus.Conforming;
}
