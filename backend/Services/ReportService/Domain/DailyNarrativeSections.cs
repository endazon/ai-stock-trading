namespace ReportService.Domain;

// FR-06, 計画 ADR-0030 決定 5・ADR-0059 フォローアップ 4, #1218, IADR-0519 決定 4, IADR-0291 決定 4: 日報の散文（LLM）を §5 市況・特記事項 と
// §6 振り返り に分ける（純関数）。LLM の呼び出しは 1 回のまま、出力を区切り行で 2 部に分けさせる（呼び出しを 2 回にすると費用と失敗の経路が倍になる）。
//
// 🔴 **散文を両節へ複製しない**（IADR-0291 決定 4: 複製は要求を満たさないのに満たしたように見える）。区切り行が無ければ全文を §5 に置き、
// §6 の散文は無いものとして扱う（推測で割らない）。区切り行が 2 つ以上あれば最初の 1 つで割り、残りの区切り行は §6 から除く。
public static class DailyNarrativeSections
{
    /// <summary>LLM に書かせる区切り行（この 1 行だけの行）。</summary>
    public const string Marker = "@@振り返り@@";

    /// <summary>散文を §5（市況）と §6（振り返り）に分ける。§6 は区切り行が無い・空なら null。</summary>
    public static (string Market, string? Review) Split(string? narrative)
    {
        if (string.IsNullOrWhiteSpace(narrative))
            return (narrative ?? string.Empty, null);

        var lines = narrative.ReplaceLineEndings("\n").Split('\n');
        var index = Array.FindIndex(lines, l => string.Equals(l.Trim(), Marker, StringComparison.Ordinal));
        if (index < 0)
            return (narrative, null);

        var market = string.Join("\n", lines[..index]).Trim();
        var review = string.Join("\n", lines[(index + 1)..].Where(l => !string.Equals(l.Trim(), Marker, StringComparison.Ordinal))).Trim();
        return (market, review.Length == 0 ? null : review);
    }
}
