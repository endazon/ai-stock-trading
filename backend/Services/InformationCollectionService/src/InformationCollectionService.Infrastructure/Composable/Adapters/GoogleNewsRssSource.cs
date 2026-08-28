using System.Globalization;
using System.Xml.Linq;
using InformationCollectionService.Application.Ports;
using InformationCollectionService.Application.State;
using InformationCollectionService.Domain;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using Microsoft.Extensions.Logging;

namespace InformationCollectionService.Infrastructure.Adapters;

// FR-01, ADR-0004, ADR-0020 決定2, IADR-0064: Google News RSS（キー不要）。**ニュース系の代替**の必須ソースである。
//
// 🔴 **公式保証が無く、高頻度ポーリングはブロックされ得る**（計画 02_datasource-candidates）。
// したがってレート制限は構成で与えた保守的な値に従い、既定でも 1 分 1 回に自制する。
//
// 取得失敗は例外として上位（SourceFetchRunner）へ返し、ソース単位の欠測として記録させる。
// 「Finnhub 企業ニュース と Google News RSS のいずれか 1 つ以上」の判定は DegradationEvaluator が行う。
internal sealed class GoogleNewsRssSource(
    HttpClient httpClient,
    IReadOnlyList<string> queries,
    IRateLimiter rateLimiter,
    ILogger<GoogleNewsRssSource> logger,
    int maxItemsPerQuery = 20)
    : IInformationSource
{
    public const string SourceName = "google-news";

    public async Task<IReadOnlyList<RawInformationItem>> FetchAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<RawInformationItem>();
        var succeededAtLeastOnce = false;
        Exception? lastFailure = null;

        foreach (var query in queries)
        {
            await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

            var url = "https://news.google.com/rss/search"
                + $"?q={Uri.EscapeDataString(query)}&hl=ja&gl=JP&ceid=JP:ja";

            try
            {
                using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                // 🔴 **解析まで通って初めて「成功」である。** 応答が返っただけで成功に数えると、
                // 壊れた XML が「取得できたが 0 件」になり、欠測が判定へ届かない。
                var parsed = Parse(query, xml, maxItemsPerQuery);
                succeededAtLeastOnce = true;
                items.AddRange(parsed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
                logger.LogWarning(ex, "Google News RSS の取得に失敗しました（クエリ {Query}）。", query);
            }
        }

        if (!succeededAtLeastOnce && lastFailure is not null)
            throw new InvalidOperationException("Google News RSS を 1 クエリも取得できなかった。", lastFailure);

        return items;
    }

    // RSS 2.0 の <channel><item> を読む。**壊れた XML は例外にしない**——解析できない応答は
    // 「取得はできたが 0 件」ではなく**欠測**として扱う（呼び出し側が例外を欠測へ写像する）。
    internal static IReadOnlyList<RawInformationItem> Parse(string query, string xml, int maxItems)
    {
        var document = XDocument.Parse(xml);
        var items = new List<RawInformationItem>();

        foreach (var element in document.Descendants("item").Take(maxItems))
        {
            var title = element.Element("title")?.Value;
            if (string.IsNullOrWhiteSpace(title))
                continue;

            items.Add(new RawInformationItem(
                InformationKind.News,
                SourceName,
                // RSS は銘柄との紐付けを持たない。**クエリを銘柄として詐称しない**——
                // 紐付けが提供側で済んでいるのは Finnhub 企業ニュースだけである（計画の採用理由）。
                Symbol: null,
                title,
                // 本文は取りに行かない（見出しと出典のみ）。再配信・スクレイピングの禁止に触れない範囲に留める。
                $"query={query};source={element.Element("source")?.Value}",
                ParsePubDate(element.Element("pubDate")?.Value),
                element.Element("link")?.Value));
        }

        return items;
    }

    // 公開時刻を読めない記事は UNIX 元期に倒す。**現在時刻に倒さない**——読めなかった記事が
    // 「今届いた最新のニュース」として並ぶほうが、古い側へ落ちるより危険である。
    private static DateTimeOffset ParsePubDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;
}
