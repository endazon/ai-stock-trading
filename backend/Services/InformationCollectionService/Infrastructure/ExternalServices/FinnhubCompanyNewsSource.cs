using System.Net.Http.Json;
using System.Text.Json.Serialization;
using InformationCollectionService.Common.Abstractions;
using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Domain;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace InformationCollectionService.Infrastructure.ExternalServices;

// FR-01, ADR-0004, ADR-0020 決定2, IADR-0064: Finnhub Free の**企業ニュース**（/api/v1/company-news）。
// ニュース系の第一の必須ソースである（銘柄との紐付けが提供側で済んでいる唯一の無料候補）。
//
// 🔴 **市況（現在値）とは別のアダプタである。** 同じ無料枠を共用するため、レート制限は構成で与えた値に従う
// （監視銘柄数の上限は日次上限から逆算する。FinnhubQuotaCalculator）。
//
// 取得失敗は**例外として上位へ返す**——SourceFetchRunner がソース単位の欠測として記録し、
// ADR-0020 決定3 の「ニュース系の全滅」判定へ渡す。**握りつぶすと欠測が判定に届かない。**
public sealed class FinnhubCompanyNewsSource(
    HttpClient httpClient,
    string apiKey,
    IReadOnlyList<string> symbols,
    IRateLimiter rateLimiter,
    IClock clock,
    ILogger<FinnhubCompanyNewsSource> logger,
    int lookbackDays = 1)
    : IInformationSource
{
    public const string SourceName = "finnhub-news";

    public async Task<IReadOnlyList<RawInformationItem>> FetchAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<RawInformationItem>();
        var to = clock.UtcNow.UtcDateTime.Date;
        var from = to.AddDays(-Math.Max(1, lookbackDays));

        var succeededAtLeastOnce = false;
        Exception? lastFailure = null;

        foreach (var symbol in symbols)
        {
            await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

            // API キーはクエリ文字列で渡す仕様のため、OTel の HttpClient 計装が URL（クエリ込み）を
            // トレースへ出力してキーが漏えいするのを防ぐべく、この要求のみ計装を抑止する（IADR-0064）。
            var url = $"https://finnhub.io/api/v1/company-news?symbol={Uri.EscapeDataString(symbol)}"
                + $"&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&token={Uri.EscapeDataString(apiKey)}";

            try
            {
                IReadOnlyList<Article>? articles;
                using (SuppressInstrumentationScope.Begin())
                {
                    using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    articles = await response.Content
                        .ReadFromJsonAsync<IReadOnlyList<Article>>(cancellationToken).ConfigureAwait(false);
                }

                succeededAtLeastOnce = true;
                items.AddRange(Map(symbol, articles));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 銘柄単位の失敗は続行する（1 銘柄のためにニュース系を全滅扱いにしない）。
                lastFailure = ex;
                logger.LogWarning(ex, "Finnhub 企業ニュースの取得に失敗しました（銘柄 {Symbol}）。", symbol);
            }
        }

        // 🔴 **1 件も成功しなかったときだけ欠測として上へ返す。** 一部成功はニュース系が生きている状態である。
        if (!succeededAtLeastOnce && lastFailure is not null)
            throw new InvalidOperationException("Finnhub 企業ニュースを 1 銘柄も取得できなかった。", lastFailure);

        return items;
    }

    private static IEnumerable<RawInformationItem> Map(string symbol, IReadOnlyList<Article>? articles)
    {
        if (articles is null)
            yield break;

        foreach (var article in articles)
        {
            if (string.IsNullOrWhiteSpace(article.Headline))
                continue;

            yield return new RawInformationItem(
                InformationKind.News,
                SourceName,
                symbol,
                article.Headline,
                // 要約が無い記事もあるため見出しへ倒す（本文を取りに行かない＝再配信の禁止に触れない）。
                string.IsNullOrWhiteSpace(article.Summary) ? article.Headline : article.Summary,
                DateTimeOffset.FromUnixTimeSeconds(article.Datetime),
                article.Url);
        }
    }

    // /company-news 応答（必要な項目のみ）。datetime は UNIX 秒。
    private sealed record Article(
        [property: JsonPropertyName("headline")] string? Headline,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("datetime")] long Datetime);
}
