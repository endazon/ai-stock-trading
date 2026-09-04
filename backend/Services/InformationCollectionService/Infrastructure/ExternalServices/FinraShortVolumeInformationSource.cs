using System.Globalization;
using InformationCollectionService.Common.Abstractions;
using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Domain;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using Microsoft.Extensions.Logging;

namespace InformationCollectionService.Infrastructure.ExternalServices;

// FR-01, ADR-0016 決定12, ADR-0020 決定1/3, #687: FINRA Daily Short Sale Volume Files（登録不要・無料・
// 当日 18:00 ET 更新）から、構成された銘柄の空売り出来高を取得する。カタログでは `finra-short` が
// Required / LimitedDegradation（LimitsShortEntriesOnly）——本ソースの取得成否がそのまま
// 「空売りの新規建てのみ停止」の判定入力になる（DegradationEvaluator・#336 で実装済み）。
//
// ファイルは日次 1 本（全銘柄を含む）で、当日ぶんは 18:00 ET 以前は未公表、週末・休場日は存在しない
// （非 2xx で現れる。一次確認では 404 ではなく 403）。**本日から遡って最初に取得できた日を採用する**
// （LookbackDays 既定 7）。実 API 前提の E2E は CI 対象外。
public sealed class FinraShortVolumeInformationSource(
    HttpClient httpClient,
    IReadOnlyList<string> symbols,
    int lookbackDays,
    IClock clock,
    IRateLimiter rateLimiter,
    ILogger<FinraShortVolumeInformationSource> logger)
    : IInformationSource
{
    // クロスプラットフォームのため OS で TZ ID を切り替える（RiskManagementService.EasternTradingDate と同方針。
    // 東部時間は夏時間を持つため BOJ コネクタのような固定オフセットは使えない）。
    private static readonly TimeZoneInfo UsEasternZone =
        TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    private const string SourceName = "finra-short";

    public async Task<IReadOnlyList<RawInformationItem>> FetchAsync(CancellationToken cancellationToken = default)
    {
        var symbolSet = new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase);
        var todayEastern = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, UsEasternZone).DateTime);

        for (var offset = 0; offset < Math.Max(1, lookbackDays); offset++)
        {
            var date = todayEastern.AddDays(-offset);
            var dateStamp = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var url = $"https://cdn.finra.org/equity/regsho/daily/CNMSshvol{dateStamp}.txt";

            await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

            using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug(
                    "FINRA 空売り出来高ファイル未公表（{Date}）: {Status}。1 日遡って再試行します。",
                    dateStamp, (int)response.StatusCode);
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Parse(body, dateStamp, symbolSet, url);
        }

        logger.LogWarning(
            "FINRA 空売り出来高ファイルを直近 {Days} 日分試行しましたが取得できませんでした。"
            + "今回の巡回では FINRA を欠測として扱います。", lookbackDays);
        return [];
    }

    private static List<RawInformationItem> Parse(
        string body, string dateStamp, HashSet<string> symbolSet, string url)
    {
        var items = new List<RawInformationItem>();
        var publishedAt = PublishedAt(dateStamp);

        // ヘッダ行: Date|Symbol|ShortVolume|ShortExemptVolume|TotalVolume|Market
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var columns = line.Split('|');
            if (columns.Length < 6 || !symbolSet.Contains(columns[1]))
                continue;

            var symbol = columns[1];
            if (!decimal.TryParse(columns[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var shortVolume) ||
                !decimal.TryParse(columns[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var totalVolume))
                continue;

            var shortExemptVolume = columns[3];
            var market = columns[5];
            var ratio = totalVolume == 0
                ? null as decimal?
                : Math.Round(shortVolume / totalVolume, 4, MidpointRounding.AwayFromZero);

            items.Add(new RawInformationItem(
                InformationKind.SupplyDemand,
                SourceName,
                symbol,
                $"FINRA 空売り出来高 {symbol}",
                $"date={dateStamp};shortVolume={shortVolume};shortExemptVolume={shortExemptVolume};" +
                $"totalVolume={totalVolume};shortVolumeRatio={(ratio?.ToString(CultureInfo.InvariantCulture) ?? "n/a")};" +
                $"market={market}",
                publishedAt,
                url));
        }

        return items;
    }

    // ADR-0016 決定12: 当日 18:00 ET 更新。採用した日の同時刻を公開時刻とする。
    private static DateTimeOffset PublishedAt(string dateStamp)
    {
        var parsed = DateTime.ParseExact(dateStamp, "yyyyMMdd", CultureInfo.InvariantCulture);
        var localNoon = new DateTime(parsed.Year, parsed.Month, parsed.Day, 18, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(localNoon, UsEasternZone.GetUtcOffset(localNoon));
    }
}
