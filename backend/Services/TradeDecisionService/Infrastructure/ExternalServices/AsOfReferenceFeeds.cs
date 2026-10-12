using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using AiStockTrading.Shared.Kernel.Trading;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-01, FR-08, FR-15, #1308, IADR-0528 決定4: as-of の参考情報（(b)）の 3 源の過去分。
// 利用者裁定 2026-10-13（#1219 のコメント）: Finnhub の company-news・SEC EDGAR・FRED（ALFRED）の過去分を本番の規則で組めば再構成できたとみなす。
//
// 文書の形は収集（InformationCollectionService の各源 → KnowledgeBaseWriterSink）と同じにする:
//   Title＝見出し・Text＝本文（要約／書式化した値）・SourceUri・Tags＝[種別, 源]・PublishedAt＝発行時刻。
// 窓の両端での切り落とし・並べ替え・上位 K 件は呼び出し側（AsOfReferenceAssembler）が行う。ここは「その期間に発行されたもの」を返す。
// どの源も、取れなければ null（取れなかった）を返す。空のリストは「その期間に無かった」である。
// 🔴 API キーは問い合わせ文字列で渡す仕様のため、OTel の HttpClient 計装が URL をトレースへ出さないよう、その要求だけ計装を止める（IADR-0064）。

/// <summary>Finnhub の企業ニュース（`/api/v1/company-news?from&to`。無料枠で約 1 年の過去分）。銘柄の源。</summary>
public sealed class FinnhubAsOfCompanyNewsFeed(
    HttpClient httpClient,
    string apiKey,
    IRateLimiter rateLimiter,
    TimeProvider timeProvider,
    ILogger<FinnhubAsOfCompanyNewsFeed> logger,
    TimeSpan? rateLimitedWait = null)
    : IAsOfReferenceFeed
{
    public const string Name = "finnhub-news";

    public string SourceName => Name;

    public AsOfReferenceCoverage Coverage => AsOfReferenceCoverage.Symbol;

    public async Task<IReadOnlyList<RetrievedContext>?> FetchAsync(
        string symbol, Market market, DateTimeOffset from, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        // 企業ニュースは北米の銘柄だけ（収集も米国の監視銘柄だけを引く）。他の市場は「無かった」。
        if (market != Market.UnitedStates)
            return [];

        // 日付は UTC の暦日で区切る（発行時刻での窓の切り落としは呼び出し側）。
        var url = $"https://finnhub.io/api/v1/company-news?symbol={Uri.EscapeDataString(symbol)}"
            + $"&from={DateOnly.FromDateTime(from.UtcDateTime):yyyy-MM-dd}&to={DateOnly.FromDateTime(at.UtcDateTime):yyyy-MM-dd}"
            + $"&token={Uri.EscapeDataString(apiKey)}";

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using (SuppressInstrumentationScope.Begin())
                {
                    using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 1)
                    {
                        // 分次の上限（他の用途と同じ鍵を共有する）。1 分待って 1 回だけ撃ち直す。
                        logger.LogWarning("Stage 0 の as-of: Finnhub の企業ニュースが 429。1 分待って撃ち直します: {Symbol}", symbol);
                        await Task.Delay(rateLimitedWait ?? TimeSpan.FromMinutes(1), timeProvider, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        logger.LogWarning("Stage 0 の as-of: Finnhub の企業ニュースの取得に失敗（{Status}）: {Symbol}", (int)response.StatusCode, symbol);
                        return null;
                    }

                    var articles = await response.Content.ReadFromJsonAsync<IReadOnlyList<Article>>(cancellationToken).ConfigureAwait(false);
                    return [.. (articles ?? [])
                        .Where(a => !string.IsNullOrWhiteSpace(a.Headline))
                        .Select(a => new RetrievedContext(
                            a.Headline!,
                            string.IsNullOrWhiteSpace(a.Summary) ? a.Headline! : a.Summary!,
                            a.Url,
                            Tags: ["News", Name],
                            PublishedAt: a.Datetime is { } seconds && seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null))];
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Stage 0 の as-of: Finnhub の企業ニュースの取得で例外: {Symbol}", symbol);
                return null;
            }
        }

        return null;
    }

    private sealed record Article(
        [property: JsonPropertyName("headline")] string? Headline,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("datetime")] long? Datetime);
}

/// <summary>
/// SEC EDGAR の提出一覧（`data.sec.gov/submissions/CIK##########.json`。受理時刻つき・鍵不要）。銘柄の源。
/// 構成した CIK だけを引く（収集と同じ。CIK の無い銘柄は「無かった」）。CIK ごとに 1 回だけ取り、記録の間は覚える。
/// </summary>
public sealed class SecEdgarAsOfFilingFeed(
    HttpClient httpClient,
    string userAgent,
    IReadOnlyList<string> ciks,
    ILogger<SecEdgarAsOfFilingFeed> logger)
    : IAsOfReferenceFeed
{
    public const string Name = "sec-edgar";

    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<(string? Ticker, RetrievedContext Doc)>?>>> _byCik = new();

    public string SourceName => Name;

    public AsOfReferenceCoverage Coverage => AsOfReferenceCoverage.Symbol;

    public async Task<IReadOnlyList<RetrievedContext>?> FetchAsync(
        string symbol, Market market, DateTimeOffset from, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        var docs = new List<RetrievedContext>();
        foreach (var cik in ciks.Select(NormalizeCik).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            var filings = await _byCik.GetOrAdd(cik, c => new Lazy<Task<IReadOnlyList<(string?, RetrievedContext)>?>>(
                () => LoadAsync(c, CancellationToken.None))).Value.ConfigureAwait(false);
            if (filings is null)
            {
                _byCik.TryRemove(cik, out _); // 失敗は覚えない（次の判断時点で撃ち直す）
                return null;
            }

            docs.AddRange(filings
                .Where(f => string.Equals(f.Ticker, symbol, StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Doc));
        }

        return docs;
    }

    private async Task<IReadOnlyList<(string? Ticker, RetrievedContext Doc)>?> LoadAsync(string cik, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://data.sec.gov/submissions/CIK{cik}.json");
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Stage 0 の as-of: SEC EDGAR の取得に失敗（CIK {Cik}）: {Status}", cik, (int)response.StatusCode);
                return null;
            }

            var submissions = await response.Content.ReadFromJsonAsync<Submissions>(cancellationToken).ConfigureAwait(false);
            if (submissions?.Filings?.Recent is not { } recent)
                return null;

            var company = submissions.Name ?? cik;
            var ticker = submissions.Tickers?.FirstOrDefault();
            var result = new List<(string?, RetrievedContext)>();
            for (var i = 0; i < (recent.AccessionNumber?.Count ?? 0); i++)
            {
                var form = At(recent.Form, i) ?? "";
                var filingDate = At(recent.FilingDate, i) ?? "";
                result.Add((ticker, new RetrievedContext(
                    $"{company} {form}",
                    $"form={form};filed={filingDate};reportDate={At(recent.ReportDate, i) ?? ""};description={At(recent.PrimaryDocDescription, i) ?? ""}",
                    DocumentUrl(cik, At(recent.AccessionNumber, i), At(recent.PrimaryDocument, i)),
                    Tags: ["Disclosure", Name],
                    // 🔴 受理時刻を優先し、無ければ提出日。どちらも読めなければ null（時点に置けない。収集は「今」で埋めるが as-of では埋めない）。
                    PublishedAt: PublishedAt(At(recent.AcceptanceDateTime, i), filingDate))));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Stage 0 の as-of: SEC EDGAR の取得で例外（CIK {Cik}）", cik);
            return null;
        }
    }

    internal static DateTimeOffset? PublishedAt(string? acceptance, string filingDate)
    {
        if (DateTimeOffset.TryParse(acceptance, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var accepted))
            return accepted.ToUniversalTime();
        if (DateTimeOffset.TryParse(filingDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var filed))
            return filed.ToUniversalTime();
        return null;
    }

    private static string? DocumentUrl(string cik, string? accession, string? primaryDocument) =>
        string.IsNullOrWhiteSpace(accession) || string.IsNullOrWhiteSpace(primaryDocument)
            ? null
            : $"https://www.sec.gov/Archives/edgar/data/{cik.TrimStart('0')}/{accession.Replace("-", "", StringComparison.Ordinal)}/{primaryDocument}";

    private static string? At(IReadOnlyList<string>? values, int index) =>
        values is not null && index < values.Count ? values[index] : null;

    // "320193" / "CIK0000320193" のどちらでも 10 桁ゼロ埋めへ正規化する。数字が無ければ null。
    internal static string? NormalizeCik(string? cik)
    {
        var digits = new string((cik ?? "").Where(char.IsAsciiDigit).ToArray());
        return digits.Length is 0 or > 10 ? null : digits.PadLeft(10, '0');
    }

    private sealed record Submissions(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("tickers")] IReadOnlyList<string>? Tickers,
        [property: JsonPropertyName("filings")] FilingsBlock? Filings);

    private sealed record FilingsBlock([property: JsonPropertyName("recent")] Recent? Recent);

    private sealed record Recent(
        [property: JsonPropertyName("accessionNumber")] IReadOnlyList<string>? AccessionNumber,
        [property: JsonPropertyName("filingDate")] IReadOnlyList<string>? FilingDate,
        [property: JsonPropertyName("reportDate")] IReadOnlyList<string>? ReportDate,
        [property: JsonPropertyName("acceptanceDateTime")] IReadOnlyList<string>? AcceptanceDateTime,
        [property: JsonPropertyName("form")] IReadOnlyList<string>? Form,
        [property: JsonPropertyName("primaryDocument")] IReadOnlyList<string>? PrimaryDocument,
        [property: JsonPropertyName("primaryDocDescription")] IReadOnlyList<string>? PrimaryDocDescription);
}

/// <summary>
/// FRED の観測を ALFRED の版で読む（`realtime_start = realtime_end = 判断日の前日`。当時公表済みの値だけ）。市場の源。
/// 🔴 版を判断日の前日にするのは、判断日の公表が判断時点より後かもしれないため（先読みしない側へ寄せる）。
/// 市場の源は銘柄に依らないので、系列 × 判断日ごとに 1 回だけ取り、記録の間は覚える（12 銘柄で同じ日を撃ち直さない）。
/// </summary>
public sealed class FredAsOfObservationFeed(
    HttpClient httpClient,
    string apiKey,
    IReadOnlyList<string> seriesIds,
    ILogger<FredAsOfObservationFeed> logger)
    : IAsOfReferenceFeed
{
    public const string Name = "fred";

    private readonly ConcurrentDictionary<(string Series, DateOnly AsOf), Lazy<Task<IReadOnlyList<RetrievedContext>?>>> _cache = new();

    public string SourceName => Name;

    public AsOfReferenceCoverage Coverage => AsOfReferenceCoverage.Market;

    public async Task<IReadOnlyList<RetrievedContext>?> FetchAsync(
        string symbol, Market market, DateTimeOffset from, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        // 判断日（米国の現地日付）。版はその前日。
        var asOf = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, MarketHours.ZoneOf(Market.UnitedStates)).DateTime);
        var observationStart = DateOnly.FromDateTime(from.UtcDateTime).AddDays(-1);
        var docs = new List<RetrievedContext>();
        foreach (var series in seriesIds.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.Ordinal))
        {
            var key = (series, asOf);
            var observations = await _cache.GetOrAdd(key, k => new Lazy<Task<IReadOnlyList<RetrievedContext>?>>(
                () => LoadAsync(k.Series, k.AsOf, observationStart, CancellationToken.None))).Value.ConfigureAwait(false);
            if (observations is null)
            {
                _cache.TryRemove(key, out _);
                return null;
            }

            docs.AddRange(observations);
        }

        return docs;
    }

    private async Task<IReadOnlyList<RetrievedContext>?> LoadAsync(
        string series, DateOnly asOf, DateOnly observationStart, CancellationToken cancellationToken)
    {
        var vintage = asOf.AddDays(-1);
        var url = $"https://api.stlouisfed.org/fred/series/observations?series_id={Uri.EscapeDataString(series)}"
            + $"&realtime_start={vintage:yyyy-MM-dd}&realtime_end={vintage:yyyy-MM-dd}"
            + $"&observation_start={observationStart:yyyy-MM-dd}&observation_end={asOf:yyyy-MM-dd}"
            + $"&file_type=json&api_key={Uri.EscapeDataString(apiKey)}";
        try
        {
            using (SuppressInstrumentationScope.Begin())
            {
                using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Stage 0 の as-of: FRED（ALFRED）の取得に失敗（{Series}・版 {Vintage}）: {Status}", series, vintage, (int)response.StatusCode);
                    return null;
                }

                var payload = await response.Content.ReadFromJsonAsync<Observations>(cancellationToken).ConfigureAwait(false);
                return [.. (payload?.ObservationList ?? [])
                    .Where(o => decimal.TryParse(o.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out _)
                        && DateOnly.TryParseExact(o.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    .Select(o => new RetrievedContext(
                        $"FRED {series}",
                        $"seriesId={series};value={o.Value};date={o.Date}",
                        $"https://fred.stlouisfed.org/series/{series}",
                        Tags: ["MacroIndicator", Name],
                        // 収集と同じく観測日を発行時刻にする（本番の KB も同じ値で新しさを切る）。
                        PublishedAt: new DateTimeOffset(
                            DateOnly.ParseExact(o.Date!, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue),
                            TimeSpan.Zero)))];
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Stage 0 の as-of: FRED（ALFRED）の取得で例外（{Series}）", series);
            return null;
        }
    }

    private sealed record Observations([property: JsonPropertyName("observations")] IReadOnlyList<Observation>? ObservationList);

    private sealed record Observation(
        [property: JsonPropertyName("date")] string? Date,
        [property: JsonPropertyName("value")] string? Value);
}
