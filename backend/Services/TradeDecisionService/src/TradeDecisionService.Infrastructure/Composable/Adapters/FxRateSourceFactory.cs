using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;

// FR-10, FR-17, #257, IADR-0107 決定5: 構成 Fx:Provider による為替レート源の選択。
// 安全既定は no-op（外部接続しない）。形は MarketDataSourceFactory（IADR-0068）に揃える。
//
// 構成不備（キー無し・未知の provider）は**起動を失敗させず** no-op へ倒す: レートが無ければ非基準通貨の
// 新規建てが見送られる（＝過大発注を招かない安全側）ため、落とすより安全。ただし「有効化したつもりで
// 効いていない」に気づけるよう必ず警告を出す。
internal static class FxRateSourceFactory
{
    public const string None = "none";
    public const string Fred = "fred";

    public static IFxRateSource Create(
        FxOptions options,
        HttpClient httpClient,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);

        var logger = loggerFactory.CreateLogger(typeof(FxRateSourceFactory).FullName!);

        switch (Provider(options))
        {
            case "":
            case None:
                // 既定。差し替え漏れの警告は NoOpFxRateSource 自身が初回 1 回だけ出す。
                return NoOp(loggerFactory);

            case Fred:
                if (string.IsNullOrWhiteSpace(options.Fred.ApiKey))
                {
                    logger.LogWarning(
                        "Fx:Provider に fred が指定されていますが、APIキー（Fx:Fred:ApiKey）が未設定のため" +
                        "為替レートを取得しません（no-op へフォールバック・IADR-0107）。");
                    return NoOp(loggerFactory);
                }

                if (options.MaxRateAgeDays > FxOptions.MaxAllowedRateAgeDays)
                {
                    logger.LogWarning(
                        "Fx:MaxRateAgeDays（{Configured} 日）は上限 {Max} 日を超えるため丸めます。" +
                        "公表周期（DEXJPUS＝週次）で説明できない古さの観測は採らない（IADR-0112 決定2）。",
                        options.MaxRateAgeDays, FxOptions.MaxAllowedRateAgeDays);
                }

                return new CachingFxRateSource(
                    new FredFxRateSource(
                        httpClient,
                        options.Fred.ApiKey,
                        string.IsNullOrWhiteSpace(options.Fred.SeriesId)
                            ? FredFxRateSource.DefaultSeriesId
                            : options.Fred.SeriesId,
                        Limiter(options.Fred.RequestsPerMinute, timeProvider),
                        loggerFactory.CreateLogger<FredFxRateSource>(),
                        string.IsNullOrWhiteSpace(options.Fred.BaseUrl)
                            ? FredFxRateSource.DefaultBaseUrl
                            : options.Fred.BaseUrl),
                    Ttl(options),
                    ResolveMaxRateAge(options),
                    timeProvider,
                    loggerFactory.CreateLogger<CachingFxRateSource>());

            default:
                logger.LogWarning(
                    "未知の Fx:Provider '{Provider}' のため為替レートを取得しません（安全既定・IADR-0107）。",
                    Provider(options));
                return NoOp(loggerFactory);
        }
    }

    /// <summary>
    /// 実際に選択される provider 名（実効構成の自己申告用）。構成不備で no-op へ倒れる場合は "none" を返し、
    /// <see cref="Create"/> と同じ規則を単一情報源にする（申告と実体がずれると検知そのものが嘘になる）。
    /// </summary>
    public static string ResolveProvider(FxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return Provider(options) switch
        {
            Fred when !string.IsNullOrWhiteSpace(options.Fred.ApiKey) => Fred,
            _ => None,
        };
    }

    /// <summary>
    /// 実際に適用される鮮度上限（#271, IADR-0112）。両側でクランプする。
    /// 下側: 0 以下は「無制限」ではなく既定へ倒す（構成ミスで歯止めを失わない）。
    /// 上側: <see cref="FxOptions.MaxAllowedRateAgeDays"/> 超は丸める（設定で guard を実質無効化させない）。
    /// <see cref="Create"/> と同じ規則を単一情報源にする（申告・テストと実体をずらさない）。
    /// </summary>
    internal static TimeSpan ResolveMaxRateAge(FxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var days = options.MaxRateAgeDays > 0 ? options.MaxRateAgeDays : FxOptions.DefaultMaxRateAgeDays;
        return TimeSpan.FromDays(Math.Min(days, FxOptions.MaxAllowedRateAgeDays));
    }

    private static string Provider(FxOptions options) => (options.Provider ?? "").Trim().ToLowerInvariant();

    // 0 以下の構成は「無制限」ではなく既定値へ倒す（構成ミスでレート予算の歯止めを失わない）。
    private static TimeSpan Ttl(FxOptions options) =>
        TimeSpan.FromSeconds(options.CacheTtlSeconds > 0 ? options.CacheTtlSeconds : new FxOptions().CacheTtlSeconds);

    private static IFxRateSource NoOp(ILoggerFactory loggerFactory) =>
        new NoOpFxRateSource(loggerFactory.CreateLogger<NoOpFxRateSource>());

    // IADR-0064: 公表上限（FRED = 120回/分）に対しサービスごとの予算を配る（既定 5回/分）。
    // 0 以下の指定は「無制限」ではなく最小の 1 回/分へクランプする（構成ミスで枠を焼き切らない・fail-safe）。
    private static IRateLimiter Limiter(int requestsPerMinute, TimeProvider timeProvider) =>
        new DelayingRateLimiter(
            new TokenBucket(Math.Max(1, requestsPerMinute), TimeSpan.FromMinutes(1)), timeProvider);
}
