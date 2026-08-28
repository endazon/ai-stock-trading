using System.Collections.Immutable;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using TradeDecisionService.Application.Ports;
using Microsoft.Extensions.Logging;

namespace TradeDecisionService.Infrastructure.Adapters;

// FR-10, FR-17, #257, IADR-0107 決定5: 構成 Fx:Provider による為替レート源の選択。
// 安全既定は no-op（外部接続しない）。形は MarketDataSourceFactory（IADR-0068）に揃える。
//
// 構成不備（キー無し・未知の provider）は**起動を失敗させず** no-op へ倒す: レートが無ければ非基準通貨の
// 新規建てが見送られる（＝過大発注を招かない安全側）ため、落とすより安全。ただし「有効化したつもりで
// 効いていない」に気づけるよう必ず警告を出す。
internal static class FxRateSourceFactory
{
    public const string None = "none";

    /// <summary>
    /// 日本銀行 時系列統計データ（**第一の情報源**・ADR-0022 決定1）。**API キーを要さない**ため、
    /// <c>Fx:Provider: "boj"</c> だけで動く（FRED と違い認証不要）。FRED が構成されていれば
    /// 順位つきフォールバックとして後段に付く（#381・IADR-0194 決定4）。
    /// </summary>
    /// <remarks>
    /// 🔴 リテラルは <see cref="FxSourceCredits.BojSourceName"/> を参照する（#381 供給結線・IADR-0199）。
    /// **表示側（報告書）はイベントに載ったこの名前で出典を引く**ため、
    /// **両側に別々のリテラルを置くと、名前を変えたときに出典だけが静かに出なくなる。**
    /// </remarks>
    public const string Boj = FxSourceCredits.BojSourceName;

    public const string Fred = "fred";

    /// <summary>
    /// 実装が受け付ける provider 名の**単一情報源**（未接続を表す <see cref="None"/> を含む）。
    /// <para>
    /// FR-10, ADR-0022, IADR-0135 決定6: 計画適合検査（<c>ActualDefaults</c>）は為替レート源の集合を
    /// **本メンバだけ**から読む。型の <c>public const string</c> を全件収集する形だと、provider と無関係な
    /// 定数（構成キー名・ログ接頭辞など）を足した瞬間に黙って provider として数えられ、
    /// **統制の検査機構が誤った実際値のまま緑になる**（最悪の失敗モード）。
    /// </para>
    /// <para>
    /// 同時に、本メンバは**飾りではなく実装の分岐そのものが通る関門**である。
    /// <see cref="Create"/> / <see cref="ResolveProvider"/> はいずれも <see cref="SelectProvider"/> を
    /// 経由し、本集合に無い名前は <c>case</c> を書いても到達しない。よって「検査が読む集合」と
    /// 「実装が実際に受け付ける集合」は構造的に乖離できない（集合への追加を忘れた provider は動かない）。
    /// </para>
    /// </summary>
    public static readonly ImmutableArray<string> ProviderNames = [None, Boj, Fred];

    /// <summary>
    /// <see cref="ProviderNames"/> に属さない provider 指定を表す番兵。
    /// 識別子として使われ得ない形にして、正規の名前と衝突させない。
    /// </summary>
    private const string Unknown = "?unknown";

    public static IFxRateSource Create(
        FxOptions options,
        HttpClient httpClient,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        IFxSourceStatusNotifier? statusNotifier = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var logger = loggerFactory.CreateLogger(typeof(FxRateSourceFactory).FullName!);

        switch (SelectProvider(options))
        {
            case None:
                // 既定（未指定を含む）。差し替え漏れの警告は NoOpFxRateSource 自身が初回 1 回だけ出す。
                return NoOp(loggerFactory);

            case Boj:
                WarnIfMaxRateAgeRounded(options, logger);

                // #381, ADR-0022 決定1・2, IADR-0194 決定4: 日銀を第一とし、FRED が構成されていれば
                // 順位つきフォールバックとして後段に付ける。**FRED のキーが無くても日銀単独で動く**
                // （日銀は認証不要）。冗長化が無い状態であることは分かるよう警告を出す。
                var ordered = new List<(string Name, IFxRateSource Source)>
                {
                    (Boj, CreateBoj(options, httpClient, timeProvider, loggerFactory)),
                };

                if (!string.IsNullOrWhiteSpace(options.Fred.ApiKey))
                {
                    ordered.Add((Fred, CreateFred(options, httpClient, timeProvider, loggerFactory)));
                }
                else
                {
                    logger.LogWarning(
                        "Fx:Provider に boj が指定されていますが、FRED の APIキー（Fx:Fred:ApiKey）が未設定のため" +
                        "フォールバックがありません。日銀側の障害・仕様変更で為替の取得が完全に止まります" +
                        "（ADR-0022 決定2 は冗長化を求めている）。");
                }

                return Cached(
                    ordered.Count == 1
                        ? ordered[0].Source
                        : new FallbackFxRateSource(
                            ordered, loggerFactory.CreateLogger<FallbackFxRateSource>(), statusNotifier),
                    options, timeProvider, loggerFactory, statusNotifier);

            case Fred:
                if (string.IsNullOrWhiteSpace(options.Fred.ApiKey))
                {
                    logger.LogWarning(
                        "Fx:Provider に fred が指定されていますが、APIキー（Fx:Fred:ApiKey）が未設定のため" +
                        "為替レートを取得しません（no-op へフォールバック・IADR-0107）。");
                    return NoOp(loggerFactory);
                }

                WarnIfMaxRateAgeRounded(options, logger);

                return Cached(
                    CreateFred(options, httpClient, timeProvider, loggerFactory),
                    options, timeProvider, loggerFactory, statusNotifier);

            default:
                logger.LogWarning(
                    "未知の Fx:Provider '{Provider}' のため為替レートを取得しません（安全既定・IADR-0107）。",
                    RequestedProvider(options));
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

        return SelectProvider(options) switch
        {
            // 日銀は認証不要のため、キーの有無で no-op へ倒れることはない（#381・IADR-0194 決定5）。
            Boj => Boj,
            Fred when !string.IsNullOrWhiteSpace(options.Fred.ApiKey) => Fred,
            _ => None,
        };
    }

    /// <summary>
    /// 鮮度上限の丸めを警告する。<see cref="Create"/> の各分岐で同じ規則を使うため切り出した
    /// （分岐ごとに書くと、片方だけ直して静かにずれる）。
    /// </summary>
    private static void WarnIfMaxRateAgeRounded(FxOptions options, ILogger logger)
    {
        if (options.MaxRateAgeDays > FxOptions.MaxAllowedRateAgeDays)
        {
            logger.LogWarning(
                "Fx:MaxRateAgeDays（{Configured} 日）は上限 {Max} 日を超えるため丸めます。" +
                "計画 ADR-0022 決定5 の絶対上限（30 日）を構成で超えさせない（IADR-0112 決定2 / IADR-0174 決定2）。",
                options.MaxRateAgeDays, FxOptions.MaxAllowedRateAgeDays);
        }
    }

    /// <summary>
    /// 鮮度判定（TTL・5 日超の警告・30 日超の停止）で包む。
    /// <para>
    /// 🔴 <b>合成の最外に置く。</b> 3 段縮退は<b>採用された値</b>に対して効くべきである。
    /// フォールバックの内側に置くと源ごとに別々の警告が出て、<b>どちらの値が使われたのか読めなくなる</b>
    /// （IADR-0194 決定4）。
    /// </para>
    /// </summary>
    private static IFxRateSource Cached(
        IFxRateSource inner,
        FxOptions options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        IFxSourceStatusNotifier? statusNotifier) =>
        new CachingFxRateSource(
            inner,
            Ttl(options),
            ResolveMaxRateAge(options),
            ResolveStaleRateWarning(options),
            timeProvider,
            loggerFactory.CreateLogger<CachingFxRateSource>(),
            statusNotifier);

    private static BojFxRateSource CreateBoj(
        FxOptions options, HttpClient httpClient, TimeProvider timeProvider, ILoggerFactory loggerFactory) =>
        new(
            httpClient,
            string.IsNullOrWhiteSpace(options.Boj.SeriesCode)
                ? BojFxRateSource.DefaultSeriesCode
                : options.Boj.SeriesCode,
            Limiter(options.Boj.RequestsPerMinute, timeProvider),
            loggerFactory.CreateLogger<BojFxRateSource>(),
            string.IsNullOrWhiteSpace(options.Boj.BaseUrl) ? BojFxRateSource.DefaultBaseUrl : options.Boj.BaseUrl,
            string.IsNullOrWhiteSpace(options.Boj.Db) ? BojFxRateSource.DefaultDb : options.Boj.Db);

    private static FredFxRateSource CreateFred(
        FxOptions options, HttpClient httpClient, TimeProvider timeProvider, ILoggerFactory loggerFactory) =>
        new(
            httpClient,
            options.Fred.ApiKey!,
            string.IsNullOrWhiteSpace(options.Fred.SeriesId) ? FredFxRateSource.DefaultSeriesId : options.Fred.SeriesId,
            Limiter(options.Fred.RequestsPerMinute, timeProvider),
            loggerFactory.CreateLogger<FredFxRateSource>(),
            string.IsNullOrWhiteSpace(options.Fred.BaseUrl) ? FredFxRateSource.DefaultBaseUrl : options.Fred.BaseUrl);

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

    /// <summary>
    /// 実際に適用される<b>警告</b>しきい値（#381, IADR-0174 決定3）。上限と同じ規律で両側クランプする。
    /// 下側: 0 以下は既定（5 日）へ倒す。
    /// 上側: <b>実効の鮮度上限を超えない</b>——超えると<b>警告が一度も出ないまま停止する</b>ことになり、
    /// 「気づくための警告」という役割そのものが消える（統制は残るが、劣化に気づけなくなる）。
    /// </summary>
    internal static TimeSpan ResolveStaleRateWarning(FxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var days = options.StaleRateWarningDays > 0
            ? options.StaleRateWarningDays
            : FxOptions.DefaultStaleRateWarningDays;
        var max = ResolveMaxRateAge(options);
        return TimeSpan.FromDays(days) < max ? TimeSpan.FromDays(days) : max;
    }

    /// <summary>構成が指定した provider 名（正規化のみ。未知の名前も**そのまま**返す＝警告文の材料）。</summary>
    private static string RequestedProvider(FxOptions options) =>
        (options.Provider ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// 構成の指定を <see cref="ProviderNames"/> の名前へ解決する。未指定は <see cref="None"/>（安全既定）、
    /// 集合に無い名前は <see cref="Unknown"/> を返す。**provider の分岐は必ず本メソッドを経由する**ため、
    /// <see cref="ProviderNames"/> に無い名前は実装のどの分岐にも到達しない（IADR-0135 決定6）。
    /// </summary>
    private static string SelectProvider(FxOptions options)
    {
        var requested = RequestedProvider(options);
        if (requested.Length == 0)
        {
            return None;
        }

        return ProviderNames.Contains(requested, StringComparer.Ordinal) ? requested : Unknown;
    }

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
