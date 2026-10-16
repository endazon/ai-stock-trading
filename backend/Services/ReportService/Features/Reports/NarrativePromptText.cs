using System.Globalization;
using System.Text;
using AiStockTrading.Shared.Contracts.Llm;
using ReportService.Domain;

namespace ReportService.Features.Reports;

/// <summary>
/// FR-06, NFR-18, #1357, IADR-0540 決定 4: 散文のプロンプトへ<b>記録の文（自由文）</b>を 1 行で引用するための整形（純関数・冪等）。
/// <para>
/// 判断根拠の記録は取引判断の LLM が書いた文であり、改行・区切り行・境界語を含み得る。プロンプトの構造（行頭の箇条・日報の区切り行
/// <see cref="DailyNarrativeSections.Marker"/>・収集情報の境界語）を偽装させないよう、<b>制御文字と改行を空白へ潰し、区切り行と境界語を落とし、
/// 長さ上限で切る</b>。検閲ではなく無害化であり、文の中身は残す。
/// </para>
/// </summary>
public static class NarrativePromptText
{
    /// <summary>判断根拠の転記の長さ上限（文字数）。トークン増を抑える（IADR-0540 決定 4）。</summary>
    public const int RationaleMaxLength = 200;

    /// <summary>切り詰めたことを示す末尾。</summary>
    public const string Ellipsis = "…（以下略）";

    // 収集情報の境界語（IADR-0542 の UntrustedDataBoundary を単一の定義として使う）。
    private const string UntrustedOpenDelimiter = UntrustedDataBoundary.OpenMarker;
    private const string UntrustedCloseDelimiter = UntrustedDataBoundary.CloseMarker;

    /// <summary>1 行の引用へ整形する。null・空白のみは null（呼び出し側が未供給として描く）。</summary>
    public static string? Inline(string? text, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            // 改行・タブを含む制御文字と、行・段落の区切り（U+2028/2029）は空白へ潰す（行頭の箇条を偽装させない）。
            var space = char.IsControl(ch) || char.IsWhiteSpace(ch) || ch is '\u2028' or '\u2029';
            if (space)
            {
                if (!lastWasSpace)
                    sb.Append(' ');
                lastWasSpace = true;
                continue;
            }

            sb.Append(ch);
            lastWasSpace = false;
        }

        // 🔴 境界語の除去は**不変になるまで繰り返す**（1 回だけだと入れ子 `@@振り@@振り返り@@返り@@` の除去後に境界語が組み上がる）。
        // 除去は文字列を必ず短くするので、繰り返しは有限回で止まる。事後条件: 出力に境界語が残らない。
        var result = sb.ToString();
        string previous;
        do
        {
            previous = result;
            result = result
                .Replace(DailyNarrativeSections.Marker, string.Empty, StringComparison.Ordinal)
                .Replace(UntrustedOpenDelimiter, string.Empty, StringComparison.Ordinal)
                .Replace(UntrustedCloseDelimiter, string.Empty, StringComparison.Ordinal);
        }
        while (!string.Equals(previous, result, StringComparison.Ordinal));

        // 引用の括り（「」）を内側から閉じさせない。
        result = result.Replace('「', '『').Replace('」', '』').Trim();

        if (result.Length == 0)
            return null;

        var info = new StringInfo(result);
        return info.LengthInTextElements <= maxLength
            ? result
            : info.SubstringByTextElements(0, maxLength).TrimEnd() + Ellipsis;
    }
}
