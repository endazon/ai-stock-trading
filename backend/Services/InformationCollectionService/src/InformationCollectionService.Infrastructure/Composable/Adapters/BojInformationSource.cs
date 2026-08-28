using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using InformationCollectionService.Application.Ports;
using InformationCollectionService.Application.State;
using InformationCollectionService.Domain;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using Microsoft.Extensions.Logging;

namespace InformationCollectionService.Infrastructure.Adapters;

// FR-01, ADR-0004/0005, IADR-0064: 日銀 時系列統計データ検索サイトの API（無料・認証不要）から、構成された系列の
// 最新観測値を取得する。系列はカンマ連結で 1 要求に束ねる（「短時間における連続したアクセスは禁止」＝要求数を最小化）。
// 期間パラメータは指定しない（系列の頻度ごとに日付書式が異なり、推測すると静かに欠測するため。全期間を受けて末尾＝
// 最新観測値のみを採る・IADR-0064）。取得失敗は空を返してログする（1 巡回を止めない）。実 API 前提の E2E は CI 対象外。
internal sealed class BojInformationSource(
    HttpClient httpClient,
    string db,
    IReadOnlyList<string> seriesCodes,
    IRateLimiter rateLimiter,
    ILogger<BojInformationSource> logger)
    : IInformationSource
{
    // 利用条件で必須のクレジット表記。KB・報告書へそのまま伝播するよう本文に含める。
    private const string Credit = "このサービスは、日本銀行時系列統計データ検索サイトのAPI機能を使用しています";

    // 日銀の統計は日本時間で公表される。日本標準時は夏時間を持たないため固定オフセットで扱う。
    private static readonly TimeSpan JapanOffset = TimeSpan.FromHours(9);

    public async Task<IReadOnlyList<RawInformationItem>> FetchAsync(CancellationToken cancellationToken = default)
    {
        await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

        var codes = Uri.EscapeDataString(string.Join(',', seriesCodes));
        var url = $"https://www.stat-search.boj.or.jp/api/v1/getDataCode?format=json&lang=en" +
                  $"&db={Uri.EscapeDataString(db)}&code={codes}";

        using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "日銀 API 取得失敗: {Status}。今回の巡回では日銀を欠測として扱います。", (int)response.StatusCode);
            return [];
        }

        var payload = await response.Content.ReadFromJsonAsync<BojResponse>(cancellationToken).ConfigureAwait(false);
        if (payload?.ResultSet is not { Count: > 0 } series)
            return [];

        return series.Select(Map).OfType<RawInformationItem>().ToList();
    }

    private static RawInformationItem? Map(BojSeries series)
    {
        if (Latest(series.Values) is not { } latest)
            return null;

        var (surveyDate, value) = latest;
        var name = series.NameOfTimeSeries ?? series.SeriesCode ?? "";

        return new RawInformationItem(
            InformationKind.MacroIndicator,
            "boj",
            null,
            $"日銀統計 {name}",
            $"seriesCode={series.SeriesCode};name={name};value={value};surveyDate={surveyDate};" +
            $"unit={series.Unit};frequency={series.Frequency};category={series.Category};credit={Credit}",
            PublishedAt(series.LastUpdate),
            null);
    }

    // 末尾（＝最新の期）から、値のある直近の観測を採る。未公表の期は null で返るため。
    private static (string SurveyDate, string Value)? Latest(BojValues? values)
    {
        if (values?.Values is not { Count: > 0 } observations || values.SurveyDates is not { Count: > 0 } dates)
            return null;

        for (var i = Math.Min(observations.Count, dates.Count) - 1; i >= 0; i--)
        {
            if (observations[i].ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;

            return (dates[i].ToString(), observations[i].ToString());
        }

        return null;
    }

    // 最終更新日（LAST_UPDATE=yyyyMMdd・JST）を公開時刻とする。読めなければ現在時刻。
    private static DateTimeOffset PublishedAt(int? lastUpdate)
    {
        if (lastUpdate is > 0 && DateTime.TryParseExact(
                lastUpdate.Value.ToString(CultureInfo.InvariantCulture), "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return new DateTimeOffset(parsed, JapanOffset);

        return DateTimeOffset.UtcNow;
    }

    // 日銀 時系列統計データ API の応答（必要な項目のみ）。フィールド名は大文字・アンダースコア区切り。
    private sealed record BojResponse([property: JsonPropertyName("RESULTSET")] IReadOnlyList<BojSeries>? ResultSet);

    private sealed record BojSeries(
        [property: JsonPropertyName("SERIES_CODE")] string? SeriesCode,
        [property: JsonPropertyName("NAME_OF_TIME_SERIES")] string? NameOfTimeSeries,
        [property: JsonPropertyName("UNIT")] string? Unit,
        [property: JsonPropertyName("FREQUENCY")] string? Frequency,
        [property: JsonPropertyName("CATEGORY")] string? Category,
        [property: JsonPropertyName("LAST_UPDATE")] int? LastUpdate,
        [property: JsonPropertyName("VALUES")] BojValues? Values);

    private sealed record BojValues(
        [property: JsonPropertyName("SURVEY_DATES")] IReadOnlyList<JsonElement>? SurveyDates,
        [property: JsonPropertyName("VALUES")] IReadOnlyList<JsonElement>? Values);
}
