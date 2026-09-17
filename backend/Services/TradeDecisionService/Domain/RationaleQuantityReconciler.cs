using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TradeDecisionService.Domain;

// FR-04, FR-10, FR-11, ADR-0040 決定5・フォローアップ4, #822, IADR-0343: LLM の根拠文が株数に言及したとき、
// システムが決めた数量（新規建て＝サイジング結果・決済＝保有全量）と突合する純関数。
//
// 🔴 **数量を拘束するのは全体前提条件に登録された統制値だけである**（ADR-0040 決定5）。根拠文は数量を決めない。
// それでも根拠文が「1株単位の新規買い」と書いて 849 株を発注した記録は、後から読んで判断を再現できない。
//
// 方式（IADR-0343 決定2）: **LLM の文言は書き換えない**。食い違う言及が 1 つでもあれば、末尾へ
// 決定的なシステム注記（<see cref="NotePrefix"/> で始まる）を追記する。一致・言及なしは原文のまま。
// 検出は保守側に倒す —— 取りこぼし（食い違う記録が残る）より、誤検出（無害な注記が 1 つ増える）を選ぶ。
public static partial class RationaleQuantityReconciler
{
    /// <summary>システム注記の開始記号。LLM の文言と区別できる全角角括弧で始める。</summary>
    public const string NotePrefix = "［システム注記:";

    /// <summary>突合結果。<see cref="Mismatched"/> が true なら注記を追記した（または既に注記済みだった）。</summary>
    public readonly record struct Reconciliation(string Rationale, bool Mismatched);

    /// <summary>根拠文中の株数言及 1 件（<see cref="Text"/> は原文の一致箇所）。</summary>
    public readonly record struct ShareCountMention(string Text, long Count);

    public static Reconciliation Reconcile(string? rationale, int quantity)
    {
        var text = rationale ?? string.Empty;
        // 既に注記済みなら二重に付けない（注記自体が「N 株」を含むため再検出させない）。
        if (text.Contains(NotePrefix, StringComparison.Ordinal))
            return new Reconciliation(text, Mismatched: true);

        var mismatched = FindShareCounts(text).Where(m => m.Count != quantity).ToList();
        if (mismatched.Count == 0)
            return new Reconciliation(text, Mismatched: false);

        var mentions = string.Join("、", mismatched.Select(m => $"「{m.Text}」").Distinct(StringComparer.Ordinal));
        var note = string.Create(
            CultureInfo.InvariantCulture,
            $"{NotePrefix} 数量はシステムが統制値・保有数から決めた {quantity} 株であり、根拠文中の株数{mentions}は数量を拘束しない］");
        return new Reconciliation(text.Length == 0 ? note : $"{text} {note}", Mismatched: true);
    }

    /// <summary>根拠文中の株数言及を出現順に返す。単価表現（1株あたり・per share・株価 等）は含めない。</summary>
    public static IReadOnlyList<ShareCountMention> FindShareCounts(string? rationale)
    {
        if (string.IsNullOrEmpty(rationale))
            return [];

        var found = new List<(int Index, ShareCountMention Mention)>();
        foreach (Match m in JapaneseShareCount().Matches(rationale))
        {
            found.Add((m.Index, new ShareCountMention(m.Value, ParseCount(m.Groups["num"].Value))));
        }

        foreach (Match m in EnglishShareCount().Matches(rationale))
        {
            found.Add((m.Index, new ShareCountMention(m.Value, ParseCount(m.Groups["num"].Value))));
        }

        return found.OrderBy(f => f.Index).Select(f => f.Mention).ToList();
    }

    // 数字（半角・全角・桁区切り）または「一」＋ 株。直後が単価・別語（あたり・当たり・につき・価・式・主・券）なら除く。
    // 直前が数字・小数点なら除く（「0.5株」の「5株」を拾わない）。
    [GeneratedRegex(
        @"(?<![0-9０-９.．,，])(?<num>[0-9０-９](?:[0-9０-９]|[,，](?=[0-9０-９]))*|一)\s*株(?!\s*(?:あたり|当たり|当り|につき|価|式|主|券))",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex JapaneseShareCount();

    // 「N share(s)」「one share」。「share price」は単価表現として除く。
    [GeneratedRegex(
        @"(?<![\w.])(?<num>\d(?:\d|,(?=\d))*|one|a single)\s+shares?\b(?!\s+price)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EnglishShareCount();

    private static long ParseCount(string raw)
    {
        if (raw is "一" || raw.Equals("one", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("a single", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (c is >= '0' and <= '9')
                sb.Append(c);
            else if (c is >= '０' and <= '９')
                sb.Append((char)('0' + (c - '０')));
        }

        // 桁あふれは「数量と一致しない言及」として扱う（保守側）。
        return long.TryParse(sb.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? count
            : long.MaxValue;
    }
}
