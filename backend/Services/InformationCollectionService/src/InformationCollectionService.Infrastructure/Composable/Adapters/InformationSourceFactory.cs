using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.InformationCollection.Application.State;
using AiStockTrading.InformationCollection.Domain;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.InformationCollection.Infrastructure.Composable.Adapters;

// FR-01, ADR-0004, ADR-0020, IADR-0022/0064: 構成 Collection:Source:Provider による情報源の選択。
// 安全既定は「有効な情報源 0 件」＝外部接続しない。案A+ は複数ソースの組み合わせのため、Provider は
// カンマ区切りで複数指定できる（例: finnhub,finnhub-news,google-news,sec-edgar,fred）。
// 各ソースは 1 つずつ独立に検証し、必須構成を欠くソース・未知の provider だけを警告つきで除外する
// （他ソースは有効なまま＝1 ソースのキー切れで案A+ 全体を止めない）。
//
// 🔴 **返すのは「名前つきの情報源の並び」である。** 名前を捨てると、どの区分のソースが落ちたかを
// 欠測判定（ADR-0020 決定3）へ渡せない。名前は InformationSourceCatalog の見出しと一致させる。
//
// レート制限は**構成値**である（既定は各ソースの公表上限より保守側）。実測に基づく調整は運用開始後。
internal static class InformationSourceFactory
{
    public const string None = "none";
    public const string Finnhub = "finnhub";
    public const string FinnhubNews = FinnhubCompanyNewsSource.SourceName;
    public const string GoogleNews = GoogleNewsRssSource.SourceName;
    public const string SecEdgar = "sec-edgar";
    public const string Edinet = "edinet";
    public const string Boj = "boj";
    public const string Fred = "fred";

    public static IReadOnlyList<NamedInformationSource> Create(
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
        var sources = new List<NamedInformationSource>();

        foreach (var provider in ParseProviders(options.Provider))
        {
            var source = CreateSingle(provider, options, httpClient, clock, timeProvider, loggerFactory, logger);
            if (source is not null)
                sources.Add(new NamedInformationSource(provider, source));
        }

        // ADR-0020 §結果: Finnhub Free の無料枠は市況の冗長系と企業ニュースで共用するため、
        // **監視銘柄数の上限は日次上限から逆算される**。上限が未実測（null）なら逆算しない（推測しない）。
        LogFinnhubQuota(options, sources, logger);

        return sources;
    }

    private static void LogFinnhubQuota(
        CollectionSourceOptions options, IReadOnlyList<NamedInformationSource> sources, ILogger logger)
    {
        var requestsPerSymbol = new[] { Finnhub, FinnhubNews }
            .Count(name => sources.Any(s => s.Name == name));
        if (requestsPerSymbol == 0)
            return;

        // 開場中 30 分毎（FR-01 の既定）を 1 日 13 巡回として見積もる。
        const int CyclesPerDay = 13;
        var cap = FinnhubQuotaCalculator.MaxWatchlistSymbols(
            options.Finnhub.DailyRequestLimit, CyclesPerDay, requestsPerSymbol);

        if (cap is null)
        {
            logger.LogWarning(
                "Finnhub Free の日次要求上限が未設定（未実測）のため、監視銘柄数の上限を逆算していません。"
                + "実測後に Collection:Source:Finnhub:DailyRequestLimit を設定してください（ADR-0020 フォローアップ）。");
            return;
        }

        logger.LogInformation(
            "Finnhub Free の日次上限 {Limit} 回から逆算した監視銘柄数の上限は {Cap} 銘柄です"
            + "（1 日 {Cycles} 巡回 × 1 銘柄あたり {PerSymbol} 要求）。現在の構成は {Configured} 銘柄。",
            options.Finnhub.DailyRequestLimit, cap, CyclesPerDay, requestsPerSymbol, options.Finnhub.Symbols.Length);
    }

    // 一覧系の構成から空要素を除く（空だけなら空配列＝未設定として扱われる）。
    private static CollectionSourceOptions Normalize(CollectionSourceOptions options) => new()
    {
        Provider = options.Provider,
        DemotedToRecommended = options.DemotedToRecommended,
        Finnhub = new FinnhubOptions
        {
            ApiKey = options.Finnhub.ApiKey,
            Symbols = Clean(options.Finnhub.Symbols),
            RateLimitPerMinute = options.Finnhub.RateLimitPerMinute,
            DailyRequestLimit = options.Finnhub.DailyRequestLimit,
            NewsLookbackDays = options.Finnhub.NewsLookbackDays,
        },
        GoogleNews = new GoogleNewsOptions
        {
            Queries = Clean(options.GoogleNews.Queries),
            RateLimitPerMinute = options.GoogleNews.RateLimitPerMinute,
            MaxItemsPerQuery = options.GoogleNews.MaxItemsPerQuery,
        },
        SecEdgar = new SecEdgarOptions
        {
            UserAgent = options.SecEdgar.UserAgent,
            Ciks = Clean(options.SecEdgar.Ciks),
            RateLimitPerSecond = options.SecEdgar.RateLimitPerSecond,
        },
        Edinet = options.Edinet,
        Boj = new BojOptions
        {
            Db = options.Boj.Db,
            SeriesCodes = Clean(options.Boj.SeriesCodes),
            RateLimitPerMinute = options.Boj.RateLimitPerMinute,
        },
        Fred = new FredOptions
        {
            ApiKey = options.Fred.ApiKey,
            SeriesIds = Clean(options.Fred.SeriesIds),
            RateLimitPerMinute = options.Fred.RateLimitPerMinute,
        },
    };

    private static string[] Clean(string[] values) =>
        [.. values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim())];

    // カンマ区切り・前後空白・大文字小文字を許容する。none は「収集しない」を意味するため列挙から除く。
    internal static IEnumerable<string> ParseProviders(string? provider) =>
        (provider ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(p => p.ToLowerInvariant())
        .Where(p => p != None)
        .Distinct();

    /// <summary>
    /// ADR-0005 決定5 / ADR-0020 決定5: 構成で指定された情報源を**推奨へ一時降格**したカタログを返す。
    /// 未知の名前は警告して無視する（構成の誤字でカタログ全体を落とさない）。
    /// </summary>
    internal static InformationSourceCatalog ApplyDemotions(
        InformationSourceCatalog catalog, string? demoted, ILogger logger)
    {
        foreach (var name in ParseProviders(demoted))
        {
            if (catalog.Find(name) is null)
            {
                logger.LogWarning(
                    "Collection:Source:DemotedToRecommended に未知の情報源 '{Source}' が指定されています（無視します）。", name);
                continue;
            }

            catalog = catalog.DemoteToRecommended(name);
            logger.LogWarning(
                "情報源 {Source} を推奨へ一時降格しました（有料化等の判断が下りるまで欠測時の扱いは記録のみ・ADR-0005）。",
                name);
        }

        return catalog;
    }

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

                // IADR-0068: HTTP は共有の FinnhubQuoteClient。レート制限は構成値（既定は公称 60 回/分の 1/2）。
                return new FinnhubInformationSource(
                    new FinnhubQuoteClient(
                        httpClient, options.Finnhub.ApiKey,
                        Limiter(options.Finnhub.RateLimitPerMinute, TimeSpan.FromMinutes(1), timeProvider),
                        loggerFactory.CreateLogger<FinnhubQuoteClient>()),
                    options.Finnhub.Symbols);

            case FinnhubNews:
                if (string.IsNullOrWhiteSpace(options.Finnhub.ApiKey) || options.Finnhub.Symbols.Length == 0)
                    return Skip(logger, provider, "APIキー（Finnhub:ApiKey）と銘柄（Finnhub:Symbols）");

                // ADR-0020 決定2: ニュース系の第一。市況面と同じ無料枠を共用する。
                return new FinnhubCompanyNewsSource(
                    httpClient, options.Finnhub.ApiKey, options.Finnhub.Symbols,
                    Limiter(options.Finnhub.RateLimitPerMinute, TimeSpan.FromMinutes(1), timeProvider),
                    clock,
                    loggerFactory.CreateLogger<FinnhubCompanyNewsSource>(),
                    options.Finnhub.NewsLookbackDays);

            case GoogleNews:
                if (options.GoogleNews.Queries.Length == 0)
                    return Skip(logger, provider, "検索クエリ（GoogleNews:Queries）");

                // ADR-0020 決定2: ニュース系の代替。公式保証が無いため既定は 1 分 1 回に自制する。
                return new GoogleNewsRssSource(
                    httpClient, options.GoogleNews.Queries,
                    Limiter(options.GoogleNews.RateLimitPerMinute, TimeSpan.FromMinutes(1), timeProvider),
                    loggerFactory.CreateLogger<GoogleNewsRssSource>(),
                    options.GoogleNews.MaxItemsPerQuery);

            case SecEdgar:
                if (string.IsNullOrWhiteSpace(options.SecEdgar.UserAgent) || options.SecEdgar.Ciks.Length == 0)
                    return Skip(logger, provider, "連絡先入り User-Agent（SecEdgar:UserAgent）と CIK（SecEdgar:Ciks）");

                return new SecEdgarInformationSource(
                    httpClient, options.SecEdgar.UserAgent, options.SecEdgar.Ciks,
                    Limiter(options.SecEdgar.RateLimitPerSecond, TimeSpan.FromSeconds(1), timeProvider),
                    loggerFactory.CreateLogger<SecEdgarInformationSource>());

            case Edinet:
                if (string.IsNullOrWhiteSpace(options.Edinet.SubscriptionKey))
                    return Skip(logger, provider, "APIキー（Edinet:SubscriptionKey）");

                return new EdinetInformationSource(
                    httpClient, options.Edinet.SubscriptionKey, clock,
                    Limiter(options.Edinet.RateLimitPerMinute, TimeSpan.FromMinutes(1), timeProvider),
                    loggerFactory.CreateLogger<EdinetInformationSource>());

            case Boj:
                if (string.IsNullOrWhiteSpace(options.Boj.Db) || options.Boj.SeriesCodes.Length == 0)
                    return Skip(logger, provider, "統計分類（Boj:Db）と系列コード（Boj:SeriesCodes）");

                return new BojInformationSource(
                    httpClient, options.Boj.Db, options.Boj.SeriesCodes,
                    Limiter(options.Boj.RateLimitPerMinute, TimeSpan.FromMinutes(1), timeProvider),
                    loggerFactory.CreateLogger<BojInformationSource>());

            case Fred:
                if (string.IsNullOrWhiteSpace(options.Fred.ApiKey) || options.Fred.SeriesIds.Length == 0)
                    return Skip(logger, provider, "APIキー（Fred:ApiKey）と系列ID（Fred:SeriesIds）");

                return new FredInformationSource(
                    httpClient, options.Fred.ApiKey, options.Fred.SeriesIds,
                    Limiter(options.Fred.RateLimitPerMinute, TimeSpan.FromMinutes(1), timeProvider),
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
            "収集しません（安全既定・IADR-0022）。", provider, required);
        return null;
    }

    private static IRateLimiter Limiter(int capacity, TimeSpan refillInterval, TimeProvider timeProvider) =>
        new DelayingRateLimiter(new TokenBucket(Math.Max(1, capacity), refillInterval), timeProvider);
}
