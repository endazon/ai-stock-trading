using System.Net.Http.Json;
using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.InformationCollection.Application.State;
using AiStockTrading.InformationCollection.Domain;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.InformationCollection.Worker.Composable.Adapters;

// FR-01, ADR-0004/0005: Finnhub（案A+・無料枠）から現在値を取得する最小の情報源アダプタ。構成で明示有効化したときのみ用いる。
// 取得失敗（レート制限・一時エラー）は当該銘柄をスキップしてログする（1 巡回を止めない）。実 API 前提の E2E は CI 対象外。
// IADR-0064: 無料枠は 60 回/分のため、銘柄ごとの要求前に自制する（IRateLimiter）。
internal sealed class FinnhubInformationSource(
    HttpClient httpClient,
    string apiKey,
    IReadOnlyList<string> symbols,
    IRateLimiter rateLimiter,
    ILogger<FinnhubInformationSource> logger)
    : IInformationSource
{
    public async Task<IReadOnlyList<RawInformationItem>> FetchAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<RawInformationItem>();

        foreach (var symbol in symbols)
        {
            await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

            // API キーはヘッダー（X-Finnhub-Token）で渡す。URL クエリに入れると OTel の HttpClient 計装が
            // リクエスト URL（クエリ含む）をトレースへ出力し、キーが可観測性基盤に漏えいするため（claude-review 指摘）。
            var url = $"https://finnhub.io/api/v1/quote?symbol={Uri.EscapeDataString(symbol)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Finnhub-Token", apiKey);

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Finnhub 取得失敗（銘柄 {Symbol}）: {Status}。この銘柄をスキップします。", symbol, (int)response.StatusCode);
                continue;
            }

            var quote = await response.Content.ReadFromJsonAsync<FinnhubQuote>(cancellationToken).ConfigureAwait(false);
            if (quote is null)
                continue;

            var publishedAt = quote.T > 0
                ? DateTimeOffset.FromUnixTimeSeconds(quote.T)
                : DateTimeOffset.UtcNow;

            items.Add(new RawInformationItem(
                InformationKind.Quote,
                "finnhub",
                symbol,
                $"{symbol} 現在値",
                $"current={quote.C};high={quote.H};low={quote.L};prevClose={quote.Pc}",
                publishedAt));
        }

        return items;
    }

    // Finnhub /quote 応答（c=現在値, h=高値, l=安値, pc=前日終値, t=UNIX 時刻）。
    private sealed record FinnhubQuote(decimal C, decimal H, decimal L, decimal Pc, long T);
}
