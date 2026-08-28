using BacktestService.Features.Backtest;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using Microsoft.Extensions.Logging;

namespace BacktestService.Infrastructure.ExternalServices;

// FR-15, ADR-0004, #208, IADR-0105: 構成 Backtest:BarData:Provider による過去データ源の選択。
// 安全既定は no-op（外部接続しない）。形は現在値の MarketDataSourceFactory（IADR-0068）に揃える。
//
// 構成不備（未知の provider・不正なベース URL）は**起動を失敗させず** no-op へ倒す。バーが取れなければ
// Stage 0 は不合格になり昇格が止まる（＝安全側）ため、落とすより縮退が適切である。ただし
// 「有効化したつもりで効いていない」に気づけるよう必ず警告を出す。
//
// FR-15, ADR-0023 決定5, IADR-0156, IADR-0157, #382【米国株日足 OHLC 履歴の現況・2026-08-06 改定】
// 既定 none は「provider の設定漏れ」ではない。次の 4 点が現況であり、1 点でも落として要約すると誤読になる
// （IADR-0156 決定2 を ADR-0023 決定5 の裁定に合わせて改めたもの。IADR-0157 決定4）。
//   1. Stooq は取得不能である（JavaScript proof-of-work のボット検知チャレンジ）。ADR-0023 決定1 は
//      **回避実装を明示的に禁じた**ため実装側で取得可能にする手段は無く、決定5 でもこの扱いは変わらない。
//      候補からは削除しない（提供側の仕様が戻れば再び使える可能性がある）。
//   2. **ADR-0023 決定5（2026-08-06 の利用者裁定）で moomoo が米国株日足 OHLC の履歴源として採用され、
//      本リポジトリにアダプタ（MoomooHistoricalBarSource）がある。** 追加費用は生じない。
//   3. **ただし決定5 は「実装側で確認を要する 2 点」を本決定の前提とし、確認するまで本番のバックテストへ
//      流さないと定めた**（取得枠の単位と回復周期／前復権と ADR-0016 決定14 の費用モデルの整合）。
//      いずれも実 OpenD を要し**未了**である（docs/blocked-tasks.md A-3）。
//   4. したがって **既定は none のままであり、Stage 0 の合格判定はまだ発火しない。** 発火させるには
//      moomoo を明示的に構成する必要があり、それは上記 2 点の確認が済んでから行う。
// 「使える履歴源が無い」と書けば 2 を否定し、「moomoo で解決した」と書けば 3 を落とす。どちらの誤読も作らないこと。
//
// **provider 選択は allow-list である。** 既知の provider が**それぞれの構成の妥当性を満たしたときだけ**
// 実アダプタを返し、未知の provider はすべて no-op へ落ちる。この形を崩さないこと
// （崩すと、綴り誤り 1 つで意図しないデータ源が実接続を始める）。
public static class HistoricalBarSourceFactory
{
    public const string None = "none";
    public const string Stooq = "stooq";

    /// <summary>moomoo OpenAPI の履歴 K 線（ADR-0023 決定5）。</summary>
    public const string Moomoo = "moomoo";

    /// <summary>
    /// 構成から**実効的な** provider 名を導く（空・未知・構成不備はすべて <see cref="None"/>）。
    /// 選択規則の単一情報源であり、<see cref="Create"/> と実効構成の自己申告
    /// （GET /internal/introspection）が同じ答えを返すことを構造で保証する。
    /// </summary>
    public static string ResolveProvider(BarDataOptions? options) =>
        (options?.Provider ?? "").Trim().ToLowerInvariant() switch
        {
            Stooq when NormalizeBaseUrl(options!.Stooq.BaseUrl) is not null => Stooq,
            Moomoo when IsUsableOpenD(options!.Moomoo) => Moomoo,
            _ => None, // 未知の provider は実アダプタへ落ちない（allow-list）
        };

    /// <param name="moomooClient">
    /// OpenD への接続（ADR-0023 決定5）。実効 provider が <see cref="Moomoo"/> のときだけ composition root が与える。
    /// **実効 provider が moomoo なのに未提供なら停止する**（構成不備ではなく配線の誤りであり、黙って no-op へ
    /// 倒すと実効構成の自己申告が `moomoo` を示したまま 1 本もバーを取らない＝IADR-0105 決定5.1 の
    /// 「自己申告と実際の選択は一致する」が破れる。BrokerFactory の moomoo 未提供と同じ扱い）。
    /// </param>
    public static IHistoricalBarSource Create(
        BarDataOptions options,
        HttpClient httpClient,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        IMoomooHistoryKLineClient? moomooClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var logger = loggerFactory.CreateLogger(typeof(HistoricalBarSourceFactory).FullName!);
        var configured = (options.Provider ?? "").Trim().ToLowerInvariant();
        var resolved = ResolveProvider(options);

        if (resolved == Stooq)
        {
            return new StooqHistoricalBarSource(
                httpClient,
                Limiter(options.Stooq.RequestsPerMinute, timeProvider),
                loggerFactory.CreateLogger<StooqHistoricalBarSource>(),
                NormalizeBaseUrl(options.Stooq.BaseUrl)!);
        }

        if (resolved == Moomoo)
        {
            return moomooClient is not null
                ? new MoomooHistoricalBarSource(
                    moomooClient,
                    Limiter(options.Moomoo.RequestsPerMinute, timeProvider),
                    loggerFactory.CreateLogger<MoomooHistoricalBarSource>())
                : throw new InvalidOperationException(
                    $"{BarDataOptions.SectionName}:Provider={Moomoo} には OpenD 接続"
                    + $"（{nameof(IMoomooHistoryKLineClient)}）が必要です。常駐 OpenD と接続構成"
                    + $"（{BarDataOptions.SectionName}:Moomoo:OpenDHost / OpenDPort）を確認してください"
                    + "（ADR-0023 決定5 / IADR-0157）。");
        }

        // 以降は no-op。既定（空・none）の警告は NoOpHistoricalBarSource 自身が出すため、
        // ここでは「有効化したつもりで効いていない」構成不備だけを切り分けて警告する。
        if (configured == Stooq)
        {
            logger.LogWarning(
                "Backtest:BarData:Provider に stooq が指定されていますが、ベース URL " +
                "（Backtest:BarData:Stooq:BaseUrl = '{BaseUrl}'）が不正なため過去データを取得しません" +
                "（no-op へフォールバック・IADR-0105）。",
                options.Stooq.BaseUrl);
        }
        else if (configured == Moomoo)
        {
            logger.LogWarning(
                "Backtest:BarData:Provider に moomoo が指定されていますが、OpenD の接続先" +
                "（Backtest:BarData:Moomoo:OpenDHost = '{Host}' / OpenDPort = {Port}）が不正なため" +
                "過去データを取得しません（no-op へフォールバック・IADR-0105 / IADR-0157）。",
                options.Moomoo.OpenDHost,
                options.Moomoo.OpenDPort);
        }
        else if (configured.Length > 0 && configured != None)
        {
            logger.LogWarning(
                "未知の Backtest:BarData:Provider '{Provider}' のため過去データを取得しません（安全既定・IADR-0105）。",
                configured);
        }

        return NoOp(loggerFactory);
    }

    // OpenD の接続先が指定されているか（発注経路の MoomooPreflight と同じ観点。ここでは落とさず no-op へ倒す）。
    private static bool IsUsableOpenD(MoomooBarDataOptions moomoo) =>
        !string.IsNullOrWhiteSpace(moomoo.OpenDHost) && moomoo.OpenDPort != 0;

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
