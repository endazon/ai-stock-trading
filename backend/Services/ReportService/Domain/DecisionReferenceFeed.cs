using System.Globalization;
using System.Text;
using AiStockTrading.Shared.Contracts.Trading;

namespace ReportService.Domain;

// FR-06, FR-08, FR-16, 04_report-templates 日報 §5, #1356, IADR-0541 決定 2: 期間の取引判断（TradeDecisionMade・判断後の見送り TradeDecisionHeld）が
// **プロンプトで見た**参考情報を、出典ごとに畳んだもの（監査台帳の判断の記録から作る。報告書生成時に検索し直さない）。
//
// 🔴 数え方: Decisions＝期間の判断の件数（Made と Held の和）。うち参照の記録を持たないもの（本列の追加前）は Unrecorded、
// 取得に失敗したもの（failed）は RetrievalFailed、KB が未構成（not-configured）は NotConfigured。Items は記録された参照を出典で束ねたもの。
// **Items が空でも「参照 0 件」とは限らない**——判断が 0 件・記録が無い・取得に失敗した、を描き分ける（下の State）。
public sealed record DecisionReferenceFeed(
    int Decisions,
    int Unrecorded,
    int RetrievalFailed,
    int NotConfigured,
    IReadOnlyList<DecisionReferenceItem> Items)
{
    /// <summary>判断が 1 件も無い（参照 0 件とは別）。</summary>
    public bool NoDecisions => Decisions == 0;

    /// <summary>参照の記録を持つ判断の件数。</summary>
    public int Recorded => Decisions - Unrecorded;
}

// 出典 1 件（同じ出典を見た判断を束ねる）。SafeUrl はリンクにしてよい URL（http/https の絶対 URI。無ければ null＝リンクにしない）。
public sealed record DecisionReferenceItem(
    string Title,
    string? SafeUrl,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<string> Symbols,
    int DecisionCount,
    IReadOnlyList<string> Tags);

public static class DecisionReferenceFeedBuilder
{
    /// <summary>判断の記録 1 件（銘柄・判断の時刻〔窓で絞る鍵〕と、その判断が残した参照。<c>null</c>＝記録されていない）。</summary>
    public sealed record DecisionRecord(string Symbol, DateTimeOffset DecidedAt, DecisionReferences? References);

    /// <summary>判断の記録を出典ごとに畳む（純関数・決定的）。並びは参照した判断の件数の多い順 → 発行の新しい順 → 表題（序数）。</summary>
    public static DecisionReferenceFeed Build(IReadOnlyList<DecisionRecord> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);

        var unrecorded = 0;
        var failed = 0;
        var notConfigured = 0;
        var groups = new Dictionary<string, (DecisionReference First, HashSet<string> Symbols, int Count, DateTimeOffset? Latest, SortedSet<string> Tags)>(StringComparer.Ordinal);

        foreach (var d in decisions)
        {
            if (d.References is not { } refs)
            {
                unrecorded++;
                continue;
            }

            if (refs.RetrievalState.StartsWith("failed", StringComparison.Ordinal))
                failed++;
            else if (refs.RetrievalState.StartsWith("not-configured", StringComparison.Ordinal))
                notConfigured++;

            // 同じ判断の中で同じ出典が 2 チャンク来ても、判断の件数は 1 と数える。
            foreach (var item in refs.Items.DistinctBy(Key))
            {
                var key = Key(item);
                if (key.Length == 0)
                    continue;

                if (groups.TryGetValue(key, out var g))
                {
                    g.Symbols.Add(d.Symbol);
                    foreach (var tag in item.Tags ?? [])
                        g.Tags.Add(tag);
                    groups[key] = (g.First, g.Symbols, g.Count + 1, Max(g.Latest, item.PublishedAt), g.Tags);
                }
                else
                {
                    groups[key] = (item, new HashSet<string>(StringComparer.Ordinal) { d.Symbol }, 1, item.PublishedAt,
                        new SortedSet<string>(item.Tags ?? [], StringComparer.Ordinal));
                }
            }
        }

        var items = groups.Values
            .Select(g => new DecisionReferenceItem(
                g.First.Title.Trim(),
                ReportLinkSafety.SafeHttpUrl(g.First.SourceUri),
                g.Latest,
                [.. g.Symbols.Order(StringComparer.Ordinal)],
                g.Count,
                [.. g.Tags]))
            .OrderByDescending(i => i.DecisionCount)
            .ThenByDescending(i => i.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenBy(i => i.Title, StringComparer.Ordinal)
            .ThenBy(i => i.SafeUrl, StringComparer.Ordinal)
            .ToList();

        return new DecisionReferenceFeed(decisions.Count, unrecorded, failed, notConfigured, items);
    }

    // 出典の同一性: リンクにしてよい URL があればそれ、無ければ表題（空白を詰めた形）。
    private static string Key(DecisionReference r) =>
        ReportLinkSafety.SafeHttpUrl(r.SourceUri) is { } url
            ? "url:" + url
            : string.IsNullOrWhiteSpace(r.Title) ? string.Empty : "title:" + string.Join(' ', r.Title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : (a > b ? a : b);
}

/// <summary>
/// FR-06, NFR-18, #1356, IADR-0541 決定 3: 外部由来の表題・URL を報告書（Markdown）へ描く前の無害化（純関数）。
/// </summary>
public static class ReportLinkSafety
{
    /// <summary>リンクにする URL の上限（文字数）。</summary>
    public const int MaxUrlLength = 2048;

    /// <summary>
    /// リンクにしてよい URL。<b>http/https の絶対 URI・ホストあり・利用者情報なし・空白／制御文字／Markdown の括りを含まない・上限以内</b>だけを通す。
    /// それ以外（<c>javascript:</c>・<c>data:</c>・<c>kb://</c>・相対パス等）は <c>null</c>（リンクにしない）。
    /// </summary>
    public static string? SafeHttpUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxUrlLength)
            return null;
        if (value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '<' or '>' or '(' or ')' or '[' or ']' or '"' or '`'))
            return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return null;
        if (string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            return null;
        return uri.AbsoluteUri;
    }

    /// <summary>
    /// 表題を Markdown の 1 行の文として描ける形にする。改行・制御文字を空白へ潰し、Markdown の記号を逆斜線で無害化し、上限で切る。
    /// 空なら「（表題なし）」。
    /// </summary>
    public static string MarkdownTitle(string? title, int maxLength)
    {
        var flat = new StringBuilder();
        var lastSpace = false;
        foreach (var ch in title ?? string.Empty)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                if (!lastSpace)
                    flat.Append(' ');
                lastSpace = true;
                continue;
            }

            flat.Append(ch);
            lastSpace = false;
        }

        var text = flat.ToString().Trim();
        if (text.Length == 0)
            return "（表題なし）";

        var info = new StringInfo(text);
        if (info.LengthInTextElements > maxLength)
            text = info.SubstringByTextElements(0, maxLength).TrimEnd() + "…";

        var escaped = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '\\' or '`' or '*' or '_' or '[' or ']' or '(' or ')' or '<' or '>' or '#' or '|' or '!' or '~')
                escaped.Append('\\');
            escaped.Append(ch);
        }

        return escaped.ToString();
    }
}
