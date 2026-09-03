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
                            : options.Finnhub.BaseUrl),
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
    /// </summary>
    public static long EstimateDailyVolume(MarketDataOptions options, int pollIntervalSeconds)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Finnhub.EstimatedSymbolCount <= 0)
            return 0;

        var cyclesPerDay = FinnhubDailyVolumeEstimator.CyclesPerDay(Math.Max(1, pollIntervalSeconds));
        return new FinnhubDailyVolumeEstimator.ProcessVolume(
            "market-data-consumer", "self", options.Finnhub.EstimatedSymbolCount, 1, cyclesPerDay)
            .EstimatedDailyRequests;
    }

    /// <summary>
    /// FR-01, ADR-0031（計画）決定2〜4, IADR-0292: 当プロセスぶんの Finnhub 日次要求量を見積もり、
    /// 業務メトリクスへ記録する。<see cref="FinnhubMarketDataOptions.EstimatedSymbolCount"/>（既定 0 ＝未申告）が
    /// 0 のときは見積らない（挙動中立）。暫定日次上限（既定 300。ADR-0031 決定3）を超えても<b>送出は止めない</b>
    /// ——現時点の統制は「見積もりの可視化」であり、確定した数値上限による強制ではない。
    /// </summary>
    /// <param name="options">MarketData 構成（Finnhub.EstimatedSymbolCount を読む）。</param>
    /// <param name="pollIntervalSeconds">当サービスの実際の巡回間隔（秒）。1 日の巡回回数の算出に使う。</param>
    /// <param name="dailyVolumeGuard">暫定日次上限の構成。</param>
    /// <param name="metrics">記録先の業務メトリクス。</param>
    /// <param name="loggerFactory">警告ログの出力先。</param>
    public static void EvaluateDailyVolume(
        MarketDataOptions options,
        int pollIntervalSeconds,
        FinnhubDailyVolumeGuardOptions dailyVolumeGuard,
        BusinessMetrics metrics,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dailyVolumeGuard);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var estimatedDailyRequests = EstimateDailyVolume(options, pollIntervalSeconds);
        if (estimatedDailyRequests == 0)
            return; // 未申告（既定）は見積らない＝挙動中立。

        var result = FinnhubDailyVolumeEstimator.Evaluate(
            estimatedDailyRequests, dailyVolumeGuard.ProvisionalDailyLimit, "market-data-consumer");

        metrics.RecordFinnhubDailyVolumeEstimate(result.EstimatedDailyRequests, result.ExceedRatio * 100);

        if (result.Verdict == FinnhubDailyVolumeEstimator.Verdict.Exceeds)
        {
            var cyclesPerDay = FinnhubDailyVolumeEstimator.CyclesPerDay(Math.Max(1, pollIntervalSeconds));
            var logger = loggerFactory.CreateLogger(typeof(MarketDataSourceFactory).FullName!);
            logger.LogWarning(
                "Finnhub の日次要求見積り {Estimated} 回/日（申告銘柄数 {Symbols} × 1 巡回 1 要求 × 1 日 {Cycles} 巡回）が"
                + "暫定日次上限 {Limit} 回/日（第三者観測の前提値。実測ではない。ADR-0031 決定3）を超えています。"
                + "送出は継続します（統制は現時点では警告のみ）。監視銘柄数・巡回頻度を上げる前に日次上限の実測を検討してください。",
                result.EstimatedDailyRequests, options.Finnhub.EstimatedSymbolCount, cyclesPerDay,
                dailyVolumeGuard.ProvisionalDailyLimit);
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
