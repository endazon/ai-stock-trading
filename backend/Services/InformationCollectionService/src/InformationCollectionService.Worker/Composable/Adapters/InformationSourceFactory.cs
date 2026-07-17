using AiStockTrading.InformationCollection.Application.Adapters;
using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.InformationCollection.Worker.Composable.Adapters;

// FR-01, ADR-0004, IADR-0022/0064: 構成 Collection:Source:Provider による情報源の選択。安全既定は no-op（外部接続しない）。
// 案A+ は複数ソースの組み合わせのため、Provider はカンマ区切りで複数指定できる（例: finnhub,sec-edgar,fred）。
// 各ソースは 1 つずつ独立に検証し、必須構成を欠くソース・未知の provider だけを警告つきで除外する（他ソースは有効なまま
// ＝1 ソースのキー切れで案A+ 全体を止めない）。有効なソースが 0 件なら no-op へ倒す（IADR-0022 の安全既定）。
//
// レート制限の既定は各ソースの公表上限より保守側に置く（IADR-0064）。実測に基づく調整は運用開始後（ADR-0004 フォローアップ）。
internal static class InformationSourceFactory
{
    public const string None = "none";
    public const string Finnhub = "finnhub";
    public const string SecEdgar = "sec-edgar";
    public const string Edinet = "edinet";
    public const string Boj = "boj";
    public const string Fred = "fred";

    public static IInformationSource Create(
        CollectionSourceOptions options,
        HttpClient httpClient,
        IClock clock,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        // 環境変数の空指定（例: Collection__Source__SecEdgar__Ciks__0=""）は「未設定」として扱う。
        // 空要素を持つ配列を「設定あり」と見なすと、実体のない構成でソースが有効化されてしまう（安全既定に反する）。
        options = Normalize(options);

        var logger = loggerFactory.CreateLogger(typeof(InformationSourceFactory).FullName!);
        var sources = new List<IInformationSource>();

        foreach (var provider in ParseProviders(options.Provider))
        {
            var source = CreateSingle(provider, options, httpClient, clock, timeProvider, loggerFactory, logger);
            if (source is not null)
                sources.Add(source);
        }

        return sources.Count switch
        {
            0 => new NoOpInformationSource(),
            1 => sources[0],
            _ => new CompositeInformationSource(sources, loggerFactory.CreateLogger<CompositeInformationSource>()),
        };
    }

    // 一覧系の構成から空要素を除く（空だけなら空配列＝未設定として扱われる）。
    private static CollectionSourceOptions Normalize(CollectionSourceOptions options) => new()
    {
        Provider = options.Provider,
        Finnhub = new FinnhubOptions
        {
            ApiKey = options.Finnhub.ApiKey,
            Symbols = Clean(options.Finnhub.Symbols),
        },
        SecEdgar = new SecEdgarOptions
        {
            UserAgent = options.SecEdgar.UserAgent,
            Ciks = Clean(options.SecEdgar.Ciks),
        },
        Edinet = options.Edinet,
        Boj = new BojOptions
        {
            Db = options.Boj.Db,
            SeriesCodes = Clean(options.Boj.SeriesCodes),
        },
        Fred = new FredOptions
        {
            ApiKey = options.Fred.ApiKey,
            SeriesIds = Clean(options.Fred.SeriesIds),
        },
    };

    private static string[] Clean(string[] values) =>
        [.. values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim())];

    // カンマ区切り・前後空白・大文字小文字を許容する。none は「収集しない」を意味するため列挙から除く。
    private static IEnumerable<string> ParseProviders(string? provider) =>
        (provider ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(p => p.ToLowerInvariant())
        .Where(p => p != None)
        .Distinct();

    // 構成を欠くソースは null（＝除外）を返す。実接続を伴うため、疑わしいときは接続しない側に倒す。
    private static IInformationSource? CreateSingle(
        string provider,
        CollectionSourceOptions options,
        HttpClient httpClient,
        IClock clock,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        ILogger logger)
    {
        switch (provider)
        {
            case Finnhub:
                if (string.IsNullOrWhiteSpace(options.Finnhub.ApiKey) || options.Finnhub.Symbols.Length == 0)
                    return Skip(logger, provider, "APIキー（Finnhub:ApiKey）と銘柄（Finnhub:Symbols）");

                // Finnhub Free は 60 回/分。その 1/2 に自制する。IADR-0068: HTTP は共有の FinnhubQuoteClient。
                return new FinnhubInformationSource(
                    new FinnhubQuoteClient(
                        httpClient, options.Finnhub.ApiKey,
                        Limiter(30, TimeSpan.FromMinutes(1), timeProvider),
                        loggerFactory.CreateLogger<FinnhubQuoteClient>()),
                    options.Finnhub.Symbols);

            case SecEdgar:
                if (string.IsNullOrWhiteSpace(options.SecEdgar.UserAgent) || options.SecEdgar.Ciks.Length == 0)
                    return Skip(logger, provider, "連絡先入り User-Agent（SecEdgar:UserAgent）と CIK（SecEdgar:Ciks）");

                // SEC EDGAR は 10 回/秒/IP。その 1/2 に自制する。
                return new SecEdgarInformationSource(
                    httpClient, options.SecEdgar.UserAgent, options.SecEdgar.Ciks,
                    Limiter(5, TimeSpan.FromSeconds(1), timeProvider),
                    loggerFactory.CreateLogger<SecEdgarInformationSource>());

            case Edinet:
                if (string.IsNullOrWhiteSpace(options.Edinet.SubscriptionKey))
                    return Skip(logger, provider, "APIキー（Edinet:SubscriptionKey）");

                // EDINET はレート制限非公表。1 分 1 回程度に自制する。
                return new EdinetInformationSource(
                    httpClient, options.Edinet.SubscriptionKey, clock,
                    Limiter(1, TimeSpan.FromMinutes(1), timeProvider),
                    loggerFactory.CreateLogger<EdinetInformationSource>());

            case Boj:
                if (string.IsNullOrWhiteSpace(options.Boj.Db) || options.Boj.SeriesCodes.Length == 0)
                    return Skip(logger, provider, "統計分類（Boj:Db）と系列コード（Boj:SeriesCodes）");

                // 日銀は「短時間における連続したアクセスは禁止」。系列を 1 要求に束ねたうえで 1 分 1 回に自制する。
                return new BojInformationSource(
                    httpClient, options.Boj.Db, options.Boj.SeriesCodes,
                    Limiter(1, TimeSpan.FromMinutes(1), timeProvider),
                    loggerFactory.CreateLogger<BojInformationSource>());

            case Fred:
                if (string.IsNullOrWhiteSpace(options.Fred.ApiKey) || options.Fred.SeriesIds.Length == 0)
                    return Skip(logger, provider, "APIキー（Fred:ApiKey）と系列ID（Fred:SeriesIds）");

                // FRED は 120 回/分。その 1/2 に自制する。
                return new FredInformationSource(
                    httpClient, options.Fred.ApiKey, options.Fred.SeriesIds,
                    Limiter(60, TimeSpan.FromMinutes(1), timeProvider),
                    loggerFactory.CreateLogger<FredInformationSource>());

            default:
                logger.LogWarning(
                    "未知の Collection:Source:Provider '{Provider}' のため、この情報源は収集しません（安全既定・IADR-0022）。",
                    provider);
                return null;
        }
    }

    private static IInformationSource? Skip(ILogger logger, string provider, string required)
    {
        logger.LogWarning(
            "Collection:Source:Provider に {Provider} が指定されていますが、{Required} が未設定のため、この情報源は" +
            "収集しません（no-op へフォールバック・IADR-0022）。", provider, required);
        return null;
    }

    private static IRateLimiter Limiter(int capacity, TimeSpan refillInterval, TimeProvider timeProvider) =>
        new DelayingRateLimiter(new TokenBucket(capacity, refillInterval), timeProvider);
}
