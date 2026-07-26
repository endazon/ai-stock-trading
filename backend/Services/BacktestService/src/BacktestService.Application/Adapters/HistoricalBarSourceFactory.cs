using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Backtest.Application;

// FR-15, ADR-0004, #208, IADR-0105: 構成 Backtest:BarData:Provider による過去データ源の選択。
// 安全既定は no-op（外部接続しない）。形は現在値の MarketDataSourceFactory（IADR-0068）に揃える。
//
// 構成不備（未知の provider・不正なベース URL）は**起動を失敗させず** no-op へ倒す。バーが取れなければ
// Stage 0 は不合格になり昇格が止まる（＝安全側）ため、落とすより縮退が適切である。ただし
// 「有効化したつもりで効いていない」に気づけるよう必ず警告を出す。
public static class HistoricalBarSourceFactory
{
    public const string None = "none";
    public const string Stooq = "stooq";

    public static IHistoricalBarSource Create(
        BarDataOptions options,
        HttpClient httpClient,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var logger = loggerFactory.CreateLogger(typeof(HistoricalBarSourceFactory).FullName!);
        var provider = (options.Provider ?? "").Trim().ToLowerInvariant();

        switch (provider)
        {
            case "":
            case None:
                // 既定。差し替え漏れの警告は NoOpHistoricalBarSource 自身が初回 1 回だけ出す。
                return NoOp(loggerFactory);

            case Stooq:
                var baseUrl = string.IsNullOrWhiteSpace(options.Stooq.BaseUrl)
                    ? StooqHistoricalBarSource.DefaultBaseUrl
                    : options.Stooq.BaseUrl.Trim();

                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    logger.LogWarning(
                        "Backtest:BarData:Provider に stooq が指定されていますが、ベース URL " +
                        "（Backtest:BarData:Stooq:BaseUrl = '{BaseUrl}'）が不正なため過去データを取得しません" +
                        "（no-op へフォールバック・IADR-0105）。",
                        baseUrl);
                    return NoOp(loggerFactory);
                }

                return new StooqHistoricalBarSource(
                    httpClient,
                    Limiter(options.Stooq.RequestsPerMinute, timeProvider),
                    loggerFactory.CreateLogger<StooqHistoricalBarSource>(),
                    baseUrl);

            default:
                logger.LogWarning(
                    "未知の Backtest:BarData:Provider '{Provider}' のため過去データを取得しません（安全既定・IADR-0105）。",
                    provider);
                return NoOp(loggerFactory);
        }
    }

    private static IHistoricalBarSource NoOp(ILoggerFactory loggerFactory) =>
        new NoOpHistoricalBarSource(loggerFactory.CreateLogger<NoOpHistoricalBarSource>());

    // 構成ミスで外部サイトへ連続アクセスしないよう、0 以下の指定は「無制限」ではなく最小の 1 回/分へクランプする。
    private static IRateLimiter Limiter(int requestsPerMinute, TimeProvider timeProvider) =>
        new DelayingRateLimiter(
            new TokenBucket(Math.Max(1, requestsPerMinute), TimeSpan.FromMinutes(1)), timeProvider);
}
