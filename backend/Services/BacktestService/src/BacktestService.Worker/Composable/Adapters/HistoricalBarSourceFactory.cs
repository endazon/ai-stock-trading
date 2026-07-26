using AiStockTrading.Backtest.Application;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Backtest.Worker.Composable.Adapters;

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

    /// <summary>
    /// 構成から**実効的な** provider 名を導く（空・未知・ベース URL 不正はすべて <see cref="None"/>）。
    /// 選択規則の単一情報源であり、<see cref="Create"/> と実効構成の自己申告
    /// （GET /internal/introspection）が同じ答えを返すことを構造で保証する。
    /// </summary>
    public static string ResolveProvider(BarDataOptions? options)
    {
        var configured = (options?.Provider ?? "").Trim().ToLowerInvariant();
        return configured == Stooq && NormalizeBaseUrl(options!.Stooq.BaseUrl) is not null ? Stooq : None;
    }

    public static IHistoricalBarSource Create(
        BarDataOptions options,
        HttpClient httpClient,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var logger = loggerFactory.CreateLogger(typeof(HistoricalBarSourceFactory).FullName!);
        var configured = (options.Provider ?? "").Trim().ToLowerInvariant();

        if (ResolveProvider(options) == Stooq)
        {
            return new StooqHistoricalBarSource(
                httpClient,
                Limiter(options.Stooq.RequestsPerMinute, timeProvider),
                loggerFactory.CreateLogger<StooqHistoricalBarSource>(),
                NormalizeBaseUrl(options.Stooq.BaseUrl)!);
        }

        // 以降は no-op。既定（空・none）は差し替え漏れの警告を NoOpHistoricalBarSource 自身が出すため、
        // ここでは「有効化したつもりで効いていない」構成不備だけを切り分けて警告する。
        if (configured == Stooq)
        {
            logger.LogWarning(
                "Backtest:BarData:Provider に stooq が指定されていますが、ベース URL " +
                "（Backtest:BarData:Stooq:BaseUrl = '{BaseUrl}'）が不正なため過去データを取得しません" +
                "（no-op へフォールバック・IADR-0105）。",
                options.Stooq.BaseUrl);
        }
        else if (configured.Length > 0 && configured != None)
        {
            logger.LogWarning(
                "未知の Backtest:BarData:Provider '{Provider}' のため過去データを取得しません（安全既定・IADR-0105）。",
                configured);
        }

        return NoOp(loggerFactory);
    }

    // 空・未設定は既定 URL へ。絶対 http/https でなければ null（＝構成不備）。
    private static string? NormalizeBaseUrl(string? configuredBaseUrl)
    {
        var baseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl)
            ? StooqHistoricalBarSource.DefaultBaseUrl
            : configuredBaseUrl.Trim();

        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? baseUrl
            : null;
    }

    private static IHistoricalBarSource NoOp(ILoggerFactory loggerFactory) =>
        new NoOpHistoricalBarSource(loggerFactory.CreateLogger<NoOpHistoricalBarSource>());

    // 構成ミスで外部サイトへ連続アクセスしないよう、0 以下の指定は「無制限」ではなく最小の 1 回/分へクランプする。
    private static IRateLimiter Limiter(int requestsPerMinute, TimeProvider timeProvider) =>
        new DelayingRateLimiter(
            new TokenBucket(Math.Max(1, requestsPerMinute), TimeSpan.FromMinutes(1)), timeProvider);
}
