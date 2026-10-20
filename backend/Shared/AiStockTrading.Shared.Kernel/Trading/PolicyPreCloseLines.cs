using System.Text;
using System.Text.RegularExpressions;

namespace AiStockTrading.Shared.Kernel.Trading;

// 🔴 FR-04, FR-07, FR-10, #1396, IADR-0561 決定 4: 方針の**決まった書式の行だけ**から閉場前の手仕舞いの指定を読む（純関数・決定的）。
// 自由文は読まない（IADR-0470・IADR-0558 と同じ作法）—— 「終盤は手仕舞いを優先する」のような文を手仕舞いの有効化や持ち越しの許可と読めば、
// 書き手の意図しない建玉を持ち越す・手仕舞う。
//
// 書式（1 行に 1 つ。NFKC で全角を半角へ寄せ、前後の空白を除いてから読む。行頭の箇条書きの印 - * ・ は付けてよい）:
//   閉場前の手仕舞い: 有効            … 閉場前の手仕舞いを有効にする（構成 PreCloseFlatten:Enabled と OR。方針で無効にはできない）
//   持ち越し可: NVDA, AAPL            … 有効時に手仕舞わず残す銘柄（区切りは「,」「、」「・」「/」・空白。大文字小文字は区別しない）
// 🔴 「有効」以外の値（「無効」「する」「あり」など）は有効化として読まない（書式を 1 つに保つ。読めない値で手仕舞いを始めない）。
// 持ち越し可の行が複数あれば和集合。銘柄は英数字と「.」「-」だけを読む（自由文の語を銘柄として拾わない）。
public static class PolicyPreCloseLines
{
    private static readonly Regex FlattenLine = new(
        @"\A(?:[-*・]\s*)?閉場前の手仕舞い\s*:\s*(?<value>\S+)\s*\z", RegexOptions.CultureInvariant);

    private static readonly Regex OvernightLine = new(
        @"\A(?:[-*・]\s*)?持ち越し可\s*:\s*(?<symbols>.+)\z", RegexOptions.CultureInvariant);

    private static readonly Regex SymbolToken = new(@"\A[A-Za-z0-9][A-Za-z0-9.\-]{0,11}\z", RegexOptions.CultureInvariant);

    private static readonly char[] Separators = [',', '、', '・', '/', ' ', '\t'];

    private static readonly Regex LineBreak = new(@"\r\n|\r|\n", RegexOptions.CultureInvariant);

    /// <summary>方針に「閉場前の手仕舞い: 有効」の行があるか。</summary>
    public static bool FlattenEnabled(string? policy) =>
        Lines(policy).Select(l => FlattenLine.Match(l)).Any(m => m.Success && m.Groups["value"].Value == "有効");

    /// <summary>方針の「持ち越し可:」行の銘柄の集合（大文字へ寄せる。大文字小文字を区別しない）。行が無ければ空。</summary>
    public static IReadOnlySet<string> OvernightAllowed(string? policy)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Lines(policy))
        {
            var match = OvernightLine.Match(line);
            if (!match.Success)
                continue;

            foreach (var token in match.Groups["symbols"].Value.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                var symbol = token.Trim().TrimEnd('。');
                if (SymbolToken.IsMatch(symbol))
                    result.Add(symbol.ToUpperInvariant());
            }
        }

        return result;
    }

    private static IEnumerable<string> Lines(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
            yield break;

        foreach (var raw in LineBreak.Split(policy.Normalize(NormalizationForm.FormKC)))
            yield return raw.Trim();
    }
}
