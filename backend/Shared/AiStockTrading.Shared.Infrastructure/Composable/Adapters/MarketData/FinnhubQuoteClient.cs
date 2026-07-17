using System.Net.Http.Json;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;

// FR-01, FR-10, ADR-0004/0005, IADR-0064/0068: Finnhub /quote の呼び出しと応答解析。
//
// IADR-0068 決定 2: 抽出の境界は「HTTP 呼び出し＋応答解析」に置き、Quote への写像には置かない。この上に薄い写像を
// 2 つ載せる。
//   - FinnhubMarketDataSource（共有・IMarketDataSource）: 現在値のみを Quote へ
//   - FinnhubInformationSource（情報収集・IInformationSource）: RawInformationItem へ（high/low/prevClose を保つ）
// Quote は Price しか持たないため、情報収集側を IMarketDataSource 経由にすると収集内容（FR-01）が劣化する。
// 共有するのは「Finnhub をどう呼ぶか」であって「何を取り出すか」ではない。
//
// 非成功応答（レート制限・一時エラー）は null を返し、呼び出し側が当該銘柄をスキップできるようにする
// （1 銘柄の失敗で巡回全体を止めない）。通信例外は握りつぶさずそのまま送出する（抽出前と同じ挙動。
// IMarketDataSource としての「取得不可＝null」への翻訳は FinnhubMarketDataSource が担う）。
// 実 API 前提の検証は手動 opt-in の live 検証（CI 対象外・IADR-0049）。
public sealed class FinnhubQuoteClient(
    HttpClient httpClient,
    string apiKey,
    IRateLimiter rateLimiter,
    ILogger logger,
    string baseUrl = FinnhubQuoteClient.DefaultBaseUrl)
{
    public const string DefaultBaseUrl = "https://finnhub.io/api/v1";

    private readonly string _baseUrl = baseUrl.TrimEnd('/');

    /// <summary>1 銘柄の現在値スナップショットを取得する。非成功応答・空応答なら null。</summary>
    public async Task<FinnhubQuoteSnapshot?> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        // IADR-0064: 429 を受けてから対処するのでは規約違反そのものを防げないため、送信前に自制する。
        await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

        // API キーはヘッダー（X-Finnhub-Token）で渡す。URL クエリに入れると OTel の HttpClient 計装が
        // リクエスト URL（クエリ含む）をトレースへ出力し、キーが可観測性基盤に漏えいするため。
        var url = $"{_baseUrl}/quote?symbol={Uri.EscapeDataString(symbol)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Finnhub-Token", apiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Finnhub 取得失敗（銘柄 {Symbol}）: {Status}。この銘柄をスキップします。", symbol, (int)response.StatusCode);
            return null;
        }

        var quote = await response.Content.ReadFromJsonAsync<FinnhubQuoteResponse>(cancellationToken).ConfigureAwait(false);
        if (quote is null)
            return null;

        var asOf = quote.T > 0 ? DateTimeOffset.FromUnixTimeSeconds(quote.T) : DateTimeOffset.UtcNow;
        return new FinnhubQuoteSnapshot(symbol, quote.C, quote.H, quote.L, quote.Pc, asOf);
    }

    // Finnhub /quote 応答（c=現在値, h=高値, l=安値, pc=前日終値, t=UNIX 時刻）。
    private sealed record FinnhubQuoteResponse(decimal C, decimal H, decimal L, decimal Pc, long T);
}

/// <summary>Finnhub /quote の取得結果。Finnhub 応答の写しであり共有の抽象ではない（IADR-0068 決定 2）。</summary>
public sealed record FinnhubQuoteSnapshot(
    string Symbol,
    decimal Current,
    decimal High,
    decimal Low,
    decimal PreviousClose,
    DateTimeOffset AsOf);
