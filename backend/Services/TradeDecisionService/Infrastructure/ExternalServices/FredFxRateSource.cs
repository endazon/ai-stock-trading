using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-10, FR-17, #257, #364, ADR-0004, IADR-0064/0106/0152: FRED の為替系列（既定 DEXJPUS＝円/ドル・営業日次）から
// 直近の観測を取得する。取得できない場合はすべて null（＝レート無し）へ縮退し、呼び出し側が
// 非基準通貨の新規建てを見送る（IADR-0107 決定3）。実 FRED API 前提の検証は手動 opt-in の live 検証（IADR-0049）。
//
// #364, IADR-0152 決定2: 基準通貨は USD であり、ポート契約は「quote 通貨 1 単位あたりの基準通貨額」である。
// DEXJPUS は「1 USD あたりの円」＝ USD/JPY のため、JPY → USD の換算には**逆数**が要る。
public sealed class FredFxRateSource(
    HttpClient httpClient,
    string apiKey,
    string seriesId,
    IRateLimiter rateLimiter,
    ILogger<FredFxRateSource> logger,
    string baseUrl = FredFxRateSource.DefaultBaseUrl)
    : IFxRateSource
{
    public const string DefaultBaseUrl = "https://api.stlouisfed.org/fred";

    /// <summary>
    /// 円/ドル（1 USD あたりの円）の日次系列。基準通貨が USD であるため、JPY のレートは本系列の**逆数**で得る。
    /// </summary>
    public const string DefaultSeriesId = "DEXJPUS";

    // 直近の観測は休日・未公表で "." になり得るため、降順で取って最初の数値を採る。
    // #271, IADR-0112 決定3: 件数は**取得窓 ≥ 受容窓**を満たす必要がある。欠測は "." の観測レコードとして
    // 返ることがあり、そのぶん件数を消費するため、件数が足りないと鮮度上限を広げても窓の端にある観測へ到達できず、
    // 広げた窓が部分的に無効になる。設定できる最大の受容窓（FxOptions.MaxAllowedRateAgeDays 日）を営業日へ
    // 換算した数（暦日 × 5/7 の切り上げ）とする。リクエストは 1 回のままでレート予算・費用は変わらない。
    private const int ObservationLimit = ((FxOptions.MaxAllowedRateAgeDays * 5) + 6) / 7;

    private readonly string _baseUrl = baseUrl.TrimEnd('/');

    public async Task<FxRate?> GetRateToBaseAsync(Currency quote, CancellationToken cancellationToken = default)
    {
        // ポート契約: 基準通貨は外部へ問い合わせず必ずレート 1（IFxRateSource）。
        if (quote == MarketCurrency.Base)
            return FxRate.Identity(quote);

        // 系列は USD/JPY 固定であり、基準通貨（USD）以外に解決できるのは JPY だけである。
        // 対応しない通貨を推測で換算しない（誤ったレートでの発注を作らない）。
        if (quote != Currency.Jpy)
        {
            logger.LogWarning("FRED の FX 系列は USD/JPY のみです（要求通貨 {Quote} は解決しません）。", quote);
            return null;
        }

        // IADR-0064: 429 を受けてから対処するのでは規約違反そのものを防げないため、送信前に自制する。
        await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

        // API キーはクエリ文字列で渡す仕様のため、OTel の HttpClient 計装が URL（クエリ込み）をトレースへ
        // 出力してキーが漏えいするのを防ぐべく、この要求のみ計装を抑止する（IADR-0064）。
        var url = $"{_baseUrl}/series/observations?series_id={Uri.EscapeDataString(seriesId)}" +
                  $"&sort_order=desc&limit={ObservationLimit}&file_type=json&api_key={Uri.EscapeDataString(apiKey)}";

        Observations? payload;
        try
        {
            using (SuppressInstrumentationScope.Begin())
            {
                using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning(
                        "FRED FX 取得失敗（系列 {SeriesId}）: {Status}。レート無しとして扱います。",
                        seriesId, (int)response.StatusCode);
                    return null;
                }

                payload = await response.Content
                    .ReadFromJsonAsync<Observations>(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            // 通信・解析の失敗はレート無しへ縮退する（安全側＝非基準通貨の新規建てが止まる）。
            logger.LogWarning(ex, "FRED FX の取得に失敗しました（系列 {SeriesId}）。レート無しとして扱います。", seriesId);
            return null;
        }

        return Map(quote, payload);
    }

    // 降順の観測列から最初の有効な数値を採る。欠測（"."）・0 以下・日付不正は採らない。
    // #364, IADR-0152 決定2: 観測値（DEXJPUS ＝ 1 USD あたりの円）の**逆数**が「JPY 1 単位あたりの USD 額」である。
    // 逆数は丸めない——丸めると往復換算の誤差が片側へ偏り、統制の実効上限が系統的にずれる。
    // 0 以下の観測を先に弾いているため、ゼロ除算は構造的に起こり得ない。
    private FxRate? Map(Currency quote, Observations? payload)
    {
        if (payload?.ObservationList is not { Count: > 0 } observations)
        {
            logger.LogWarning("FRED FX 応答に観測がありません（系列 {SeriesId}）。レート無しとして扱います。", seriesId);
            return null;
        }

        foreach (var observation in observations)
        {
            if (!decimal.TryParse(observation.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var jpyPerUsd)
                || jpyPerUsd <= 0m)
            {
                continue;
            }

            if (!DateTime.TryParseExact(
                    observation.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                continue;
            }

            return new FxRate(quote, MarketCurrency.Base, 1m / jpyPerUsd, new DateTimeOffset(date, TimeSpan.Zero));
        }

        logger.LogWarning("FRED FX に有効な観測がありません（系列 {SeriesId}）。レート無しとして扱います。", seriesId);
        return null;
    }

    // FRED series/observations 応答（必要な項目のみ）。
    private sealed record Observations(
        [property: JsonPropertyName("observations")] IReadOnlyList<Observation>? ObservationList);

    private sealed record Observation(string? Date, string? Value);
}
