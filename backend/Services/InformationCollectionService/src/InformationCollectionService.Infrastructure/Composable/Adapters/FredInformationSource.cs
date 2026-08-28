using System.Globalization;
using System.Net.Http.Json;
using InformationCollectionService.Application.Ports;
using InformationCollectionService.Application.State;
using InformationCollectionService.Domain;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace InformationCollectionService.Infrastructure.Adapters;

// FR-01, ADR-0004/0005, IADR-0064: FRED（米セントルイス連銀・無料／要 API キー・120回/分）から、構成された系列の
// 最新観測値を取得する。取得失敗（レート制限・一時エラー）は当該系列をスキップしてログする（1 巡回を止めない）。
// 実 API 前提の E2E は CI 対象外。
internal sealed class FredInformationSource(
    HttpClient httpClient,
    string apiKey,
    IReadOnlyList<string> seriesIds,
    IRateLimiter rateLimiter,
    ILogger<FredInformationSource> logger)
    : IInformationSource
{
    public async Task<IReadOnlyList<RawInformationItem>> FetchAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<RawInformationItem>();

        foreach (var seriesId in seriesIds)
        {
            await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

            // 最新の 1 観測のみを要求する（無料枠・転送量の節約）。API キーはクエリ文字列で渡す仕様のため、OTel の
            // HttpClient 計装が URL（クエリ込み）をトレースへ出力してキーが漏えいするのを防ぐべく、この要求のみ
            // 計装を抑止する（IADR-0064）。
            var url = $"https://api.stlouisfed.org/fred/series/observations?series_id={Uri.EscapeDataString(seriesId)}" +
                      $"&sort_order=desc&limit=1&file_type=json&api_key={Uri.EscapeDataString(apiKey)}";

            Observations? payload;
            using (SuppressInstrumentationScope.Begin())
            {
                using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning(
                        "FRED 取得失敗（系列 {SeriesId}）: {Status}。この系列をスキップします。",
                        seriesId, (int)response.StatusCode);
                    continue;
                }

                payload = await response.Content
                    .ReadFromJsonAsync<Observations>(cancellationToken).ConfigureAwait(false);
            }

            if (Map(seriesId, payload) is { } item)
                items.Add(item);
        }

        return items;
    }

    private static RawInformationItem? Map(string seriesId, Observations? payload)
    {
        if (payload?.ObservationList is not { Count: > 0 } observations)
            return null;

        var latest = observations[0];

        // FRED は欠測を "." で返す（未公表・休日等）。数値でない観測は採らない。
        if (!decimal.TryParse(latest.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
            return null;

        return new RawInformationItem(
            InformationKind.MacroIndicator,
            "fred",
            null,
            $"FRED {seriesId}",
            $"seriesId={seriesId};value={latest.Value};date={latest.Date}",
            PublishedAt(latest.Date),
            $"https://fred.stlouisfed.org/series/{seriesId}");
    }

    // 観測日（yyyy-MM-dd）を公開時刻とする。読めなければ現在時刻。
    private static DateTimeOffset PublishedAt(string? date) =>
        DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? new DateTimeOffset(parsed, TimeSpan.Zero)
            : DateTimeOffset.UtcNow;

    // FRED series/observations 応答（必要な項目のみ）。
    private sealed record Observations(
        [property: System.Text.Json.Serialization.JsonPropertyName("observations")]
        IReadOnlyList<Observation>? ObservationList);

    private sealed record Observation(string? Date, string? Value);
}
