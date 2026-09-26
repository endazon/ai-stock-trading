using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;

// FR-10, FR-03, FR-16, #158, IADR-0068 決定 6: 構成 MarketData:Provider による現在値ソースの選択。
// 安全既定は no-op（外部接続しない）。形は情報収集の InformationSourceFactory（IADR-0022/0064）に揃える。
//
// 構成不備（キー無し・未知の provider）は**起動を失敗させず** no-op へ倒す: 現在値が取れなければ含みは 0＝
// 保守的な評価に倒れる（IADR-0066 決定 2）ため、落とすより安全側。ただし「有効化したつもりで効いていない」に
// 気づけるよう必ず警告を出す。
public static class MarketDataSourceFactory
{
    public const string None = "none";
    public const string Finnhub = "finnhub";

    public static IMarketDataSource Create(
        MarketDataOptions options,
        HttpClient httpClient,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);

        var logger = loggerFactory.CreateLogger(typeof(MarketDataSourceFactory).FullName!);
        var provider = (options.Provider ?? "").Trim().ToLowerInvariant();

        switch (provider)
        {
            case "":
            case None:
                // 既定。差し替え漏れの警告は NoOpMarketDataSource 自身が初回 1 回だけ出す。
                return NoOp(loggerFactory);

            case Finnhub:
                if (string.IsNullOrWhiteSpace(options.Finnhub.ApiKey))
                {
                    logger.LogWarning(
                        "MarketData:Provider に finnhub が指定されていますが、APIキー（MarketData:Finnhub:ApiKey）が" +
                        "未設定のため現在値を取得しません（no-op へフォールバック・IADR-0068）。");
                    return NoOp(loggerFactory);
                }

                return new FinnhubMarketDataSource(
                    new FinnhubQuoteClient(
                        httpClient,
                        options.Finnhub.ApiKey,
                        Limiter(options.Finnhub.RequestsPerMinute, timeProvider),
                        loggerFactory.CreateLogger<FinnhubQuoteClient>(),
                        string.IsNullOrWhiteSpace(options.Finnhub.BaseUrl)
                            ? FinnhubQuoteClient.DefaultBaseUrl
                            : options.Finnhub.BaseUrl,
                        timeProvider),
                    loggerFactory.CreateLogger<FinnhubMarketDataSource>());

            default:
                logger.LogWarning(
                    "未知の MarketData:Provider '{Provider}' のため現在値を取得しません（安全既定・IADR-0068）。",
                    provider);
                return NoOp(loggerFactory);
        }
    }

    /// <summary>
    /// FR-01, ADR-0031（計画）決定2〜4, IADR-0292: 当プロセスぶんの Finnhub 日次要求見積り（回/日）。
    /// <see cref="FinnhubMarketDataOptions.EstimatedSymbolCount"/>（既定 0＝未申告）が 0 なら 0（挙動中立）。
    /// introspection 自己申告（数値のみ・ログ副作用なし）から使う軽量版。
    /// ADR-0043（計画）決定 3, #1030, IADR-0437: <paramref name="activeMinutesPerDay"/> は 1 日のうち巡回する時間（分）。
    /// 既定は 24 時間（開場に関係なく巡回する）。閉場中は巡回しない市場監視は場中の長さ（米国 390 分）を渡す。
    /// </summary>
    public static long EstimateDailyVolume(
        MarketDataOptions options,
        int pollIntervalSeconds,
        int activeMinutesPerDay = FinnhubDailyVolumeEstimator.MinutesPerDay)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Finnhub.EstimatedSymbolCount <= 0)
            return 0;

        var cyclesPerDay = FinnhubDailyVolumeEstimator.CyclesPerDay(Math.Max(1, pollIntervalSeconds), activeMinutesPerDay);
        return new FinnhubDailyVolumeEstimator.ProcessVolume(
            "market-data-consumer", "self", options.Finnhub.EstimatedSymbolCount, 1, cyclesPerDay)
            .EstimatedDailyRequests;
    }

    /// <summary>
    /// FR-01, ADR-0031（計画）決定2〜4, IADR-0292: 当プロセスぶんの Finnhub 日次要求量を見積もり、
    /// 業務メトリクスへ記録する。<see cref="FinnhubMarketDataOptions.EstimatedSymbolCount"/>（既定 0 ＝未申告）が
    /// 0 のときは見積らない（挙動中立）。<b>送出は止めない</b>——日次の統制は「見積もりの可視化」と 429 の見張りである。
    /// ADR-0043（計画）決定 1・3, #1030, IADR-0437: 暫定の 300 回/日は撤回。日次上限は既定で未設定（未実測）であり、
    /// そのときは見積りを記録するだけで比べない。上限を実測して設定したときだけ、超過を警告する。
    /// </summary>
    /// <param name="options">MarketData 構成（Finnhub.EstimatedSymbolCount を読む）。</param>
    /// <param name="pollIntervalSeconds">当サービスの実際の巡回間隔（秒）。1 日の巡回回数の算出に使う。</param>
    /// <param name="dailyVolumeGuard">日次上限の構成（既定は未設定）。</param>
    /// <param name="metrics">記録先の業務メトリクス。</param>
    /// <param name="loggerFactory">警告ログの出力先。</param>
    /// <param name="activeMinutesPerDay">1 日のうち巡回する時間（分）。既定 24 時間。</param>
    public static void EvaluateDailyVolume(
        MarketDataOptions options,
        int pollIntervalSeconds,
        FinnhubDailyVolumeGuardOptions dailyVolumeGuard,
        BusinessMetrics metrics,
        ILoggerFactory loggerFactory,
        int activeMinutesPerDay = FinnhubDailyVolumeEstimator.MinutesPerDay)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dailyVolumeGuard);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var estimatedDailyRequests = EstimateDailyVolume(options, pollIntervalSeconds, activeMinutesPerDay);
        if (estimatedDailyRequests == 0)
            return; // 未申告（既定）は見積らない＝挙動中立。

        var result = FinnhubDailyVolumeEstimator.Evaluate(
            estimatedDailyRequests, dailyVolumeGuard.ProvisionalDailyLimit, "market-data-consumer");

        metrics.RecordFinnhubDailyVolumeEstimate(result.EstimatedDailyRequests, result.ExceedRatio * 100);

        var cyclesPerDay = FinnhubDailyVolumeEstimator.CyclesPerDay(Math.Max(1, pollIntervalSeconds), activeMinutesPerDay);
        var logger = loggerFactory.CreateLogger(typeof(MarketDataSourceFactory).FullName!);
        if (result.Verdict == FinnhubDailyVolumeEstimator.Verdict.Exceeds)
        {
            logger.LogWarning(
                "Finnhub の日次要求見積り {Estimated} 回/日（申告銘柄数 {Symbols} × 1 巡回 1 要求 × 1 日 {Cycles} 巡回）が"
                + "設定された日次上限 {Limit} 回/日を超えています（ADR-0031 決定3）。送出は継続します（統制は警告のみ）。",
                result.EstimatedDailyRequests, options.Finnhub.EstimatedSymbolCount, cyclesPerDay,
                dailyVolumeGuard.ProvisionalDailyLimit);
        }
        else if (result.Verdict == FinnhubDailyVolumeEstimator.Verdict.NotCompared)
        {
            logger.LogInformation(
                "Finnhub の日次要求見積り {Estimated} 回/日（申告銘柄数 {Symbols} × 1 巡回 1 要求 × 1 日 {Cycles} 巡回）。"
                + "日次上限は未実測のため比べません（ADR-0043 決定1）。日次は 429 で見張ります。",
                result.EstimatedDailyRequests, options.Finnhub.EstimatedSymbolCount, cyclesPerDay);
        }
    }

    private static IMarketDataSource NoOp(ILoggerFactory loggerFactory) =>
        new NoOpMarketDataSource(loggerFactory.CreateLogger<NoOpMarketDataSource>());

    // IADR-0064/0068: 公表上限（Finnhub Free = 60回/分）に対し、サービスごとの予算を配る（既定 10回/分）。
    // 0 以下の指定は「無制限」ではなく最小の 1 回/分へクランプする（構成ミスで枠を焼き切らない・fail-safe）。
    private static IRateLimiter Limiter(int requestsPerMinute, TimeProvider timeProvider) =>
        new DelayingRateLimiter(
            new TokenBucket(Math.Max(1, requestsPerMinute), TimeSpan.FromMinutes(1)), timeProvider);
}
