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

    private static IMarketDataSource NoOp(ILoggerFactory loggerFactory) =>
        new NoOpMarketDataSource(loggerFactory.CreateLogger<NoOpMarketDataSource>());

    // IADR-0064/0068: 公表上限（Finnhub Free = 60回/分）に対し、サービスごとの予算を配る（既定 10回/分）。
    // 0 以下の指定は「無制限」ではなく最小の 1 回/分へクランプする（構成ミスで枠を焼き切らない・fail-safe）。
    private static IRateLimiter Limiter(int requestsPerMinute, TimeProvider timeProvider) =>
        new DelayingRateLimiter(
            new TokenBucket(Math.Max(1, requestsPerMinute), TimeSpan.FromMinutes(1)), timeProvider);
}
