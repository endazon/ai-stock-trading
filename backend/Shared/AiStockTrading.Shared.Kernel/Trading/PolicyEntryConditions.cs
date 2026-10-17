using System.Text;
using System.Text.RegularExpressions;

namespace AiStockTrading.Shared.Kernel.Trading;

// FR-04, FR-07, ADR-0003, #1384, IADR-0557 決定 1: 方針の**決まった書式の「条件 X:」行だけ**から新規建ての条件の記号を読む（純関数・決定的）。
//
// 取引判断（新規建ての判断が答えた「成立した条件の記号」を、この集合に含まれるときだけ記録する）と、報告書（方針の改訂の AI へ書式を案内する）が
// **同じ部品**を引く。IADR-0470（「利確:」行）と同じく、🔴 **自由文は読まない** —— 説明の文から記号を拾うと、方針に無い記号を「方針の条件」として
// 記録し、保有中の判断がその記号の出口を探すことになる。
//
// 書式（1 行に 1 つ。NFKC で全角を半角へ寄せ、前後の空白を除いてから読む）:
//   [行頭の箇条書きの印 - * ・ のどれか][買い|売り|新規建て|空売り]条件 <英大文字 1 字>[ (<名前>)]: <説明>
//   例「条件 A: AAPL 前日比 -3% 以下で買い」「買い条件 B（急落反発）: 日中安値から +1% 戻したら買い」
// 記号は英大文字 1 字（A〜Z）に限る。読み取りの誤りより、書式を 1 つに保つほうを採る（記号の長さで判断の出力が揺れない）。
// 同じ記号の行が複数あっても集合としては 1 つである（条件の中身は判断の LLM が方針の本文から読む。ここは記号の実在だけを確かめる）。
public static class PolicyEntryConditions
{
    // 「条件」の前に付けてよい語（無くてもよい）。売買の向きを書き分ける方針（買い条件・売り条件）をそのまま読む。
    private static readonly Regex ConditionLine = new(
        @"\A(?:[-*・]\s*)?(?:買い|売り|新規建て|空売り)?条件\s*(?<symbol>[A-Z])\s*(?:\([^()]{0,40}\)\s*)?:",
        RegexOptions.CultureInvariant);

    private static readonly Regex LineBreak = new(@"\r\n|\r|\n", RegexOptions.CultureInvariant);

    /// <summary>方針の「条件 X:」行の記号の集合（英大文字 1 字。出現順を保たない）。読める行が無ければ空。</summary>
    public static IReadOnlySet<string> Symbols(string? policy)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(policy))
            return result;

        // 全角の英字・コロン・括弧・空白を半角へ寄せる（NFKC）。
        var text = policy.Normalize(NormalizationForm.FormKC);
        foreach (var raw in LineBreak.Split(text))
        {
            var match = ConditionLine.Match(raw.Trim());
            if (match.Success)
                result.Add(match.Groups["symbol"].Value);
        }

        return result;
    }

    /// <summary>
    /// 判断が答えた記号を、比較できる形（前後の空白を除き NFKC・大文字化）へ寄せる。英大文字 1 字にならなければ null（不明）。
    /// </summary>
    public static string? NormalizeSymbol(string? answered)
    {
        if (string.IsNullOrWhiteSpace(answered))
            return null;

        var s = answered.Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();
        return s.Length == 1 && s[0] is >= 'A' and <= 'Z' ? s : null;
    }
}
