using InformationCollectionService.Common.Abstractions;
using InformationCollectionService.Domain;
using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Features.InformationCollection.ActivateGeneralWebCollection;
using InformationCollectionService.Hosted;
using InformationCollectionService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.Shared.KnowledgeBase.Foundation.Extensions;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;
using Serilog;
using Wolverine;
using AppSvc = InformationCollectionService.Features.InformationCollection.RunCollectionCycle.InformationCollectionAppService;

const string ServiceName = "ai-stock-trading.information-collection-service";

// #9 Slice A, FR-01, IADR-0022: 情報収集サービス。定時ポーリングで収集→正規化→サニタイズ→KB 保存→収集完了イベント発行。
// ヘルスチェックと run-once トリガの HTTP サーフェスのため WebApplication を用いる（DB なし）。
// 外部情報源・KB 保存は既定で無効（安全既定）。
//
// IADR-0013: 本 Program.cs の standalone 配線（Wolverine/RabbitMQ を shim 経由で組む部分）は dev/test/CI での
// ローカル単体実行のためのもの。本番は platform 統合（#22）で共通基盤に置き換わる。
var builder = WebApplication.CreateBuilder(args);

// IADR-0011: 可観測性（Serilog + OTel）。
builder.Services.AddSerilog((_, logConfig) =>
    logConfig.ConfigureAiStockTradingSerilog(builder.Configuration, ServiceName));
builder.Services.AddAiStockTradingObservability(builder.Configuration, ServiceName);

// NFR（セキュリティ）, #456, IADR-0176 決定1: Keycloak OIDC/JWT 認証。**run-once は業務操作であり無認証で公開しない。**
// 本サービスは長らく `/health/*` と introspection しか持たない前提で認証を登録していなかったが、#121 で
// run-once トリガ（収集サイクルの起動）を足した際に認可が付かないまま残っていた（#450 の横断調査で発覚）。
builder.Services.AddAiStockTradingAuth(builder.Configuration);

// liveness ヘルスチェック（DB を持たない）。
builder.Services.AddAiStockTradingHealthChecks();
// ADR-0001, FR-15, #22 受け入れ基準③: 実効構成（有効な段=宣言由来・選択中ポート実装・構成バージョン）の自己申告。
// メッシュ内部限定エンドポイント GET /internal/introspection（無認可・ネットワーク分離が防御）。
builder.Services.AddAiStockTradingIntrospection(builder.Configuration, ServiceName, b => b
    // Collection:Source:Provider はカンマ区切りで複数ソース（例 "finnhub,sec-edgar"）を許容する。自己申告は
    // 構成値をそのまま実装文字列として申告する（有効化中のソース集合を忠実に反映。BFF 突合時は集合として解釈）。
    .AddPort("source", string.IsNullOrWhiteSpace(builder.Configuration["Collection:Source:Provider"]) ? "noop" : builder.Configuration["Collection:Source:Provider"]!)
    .AddPortFromBaseUrl("cost-state", builder.Configuration["CostControl:BaseUrl"], "http", "placeholder")
    .AddPortFromBaseUrl("knowledge-base-writer", builder.Configuration["KnowledgeBase:Documents:BaseUrl"], "http", "noop")
    // FR-01, FR-13, #1015, IADR-0435: Finnhub の対象銘柄の出所。MarketMonitor:BaseUrl 設定時=http（監視銘柄に追随）、
    // 未設定/不正=configuration（構成の固定リスト）。取引判断の同名ポート（IADR-0095）と同じ語彙。
    .AddPortFromBaseUrl("watchlist", builder.Configuration["MarketMonitor:BaseUrl"], "http", "configuration")
    // FR-01, ADR-0031（計画）決定2〜4, IADR-0292: Finnhub 日次要求見積り（回/日）の自己申告（現在の実現手段が
    // 無かった「日次総量の可視化」を、外部から /internal/introspection 経由で読めるようにする）。
    // FR-01, ADR-0043（計画）決定 3, #1030, IADR-0437: 監視銘柄に追随する構成（MarketMonitor:BaseUrl）では構成の固定リストは対象ではない。
    // 自己申告は起動時に 1 回だけ決まるため、1 巡回に収まる対象の数（上限）で数える。巡回ごとの実数は業務メトリクスが持つ。
    .AddMetric(
        "finnhub-daily-request-estimate",
        EstimateAtStartup(builder.Configuration).ToString()));

// 収集ポーリングの構成（間隔）。
builder.Services.Configure<CollectionOptions>(builder.Configuration.GetSection(CollectionOptions.SectionName));

// FR-01, ADR-0004: 案A+ の許可リスト（許可された情報源のみ受理）。
builder.Services.AddSingleton(SourceAllowlist.Default);

// FR-01, ADR-0020, ADR-0005 決定5: 情報源の区分表（必須/推奨/任意/検証用途と欠測時の振る舞い）。
// Collection:Source:DemotedToRecommended に列挙したソースは**推奨へ一時降格**する（有料化の判断が下りるまで）。
builder.Services.AddSingleton(sp => InformationSourceFactory.ApplyDemotions(
    InformationSourceCatalog.Default,
    sp.GetRequiredService<IConfiguration>()["Collection:Source:DemotedToRecommended"],
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("InformationSourceCatalog")));

// FR-01, IADR-0022/0064: 情報源の選択（安全既定 no-op）。実接続は Collection:Source:Provider に列挙し、かつ当該ソースの
// 必須構成（APIキー・銘柄・CIK・系列コード等）が揃ったときのみ有効になる。案A+ の複数ソースはカンマ区切りで指定する
// （例: finnhub,sec-edgar,edinet,boj,fred）。各ソースは公表レート上限より保守側に送信前自制する（IADR-0064）。
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IClock, SystemClock>();
// IADR-0068: レート制限の時刻源は共有物へ揃えるため TimeProvider（IClock は情報源の日付計算で引き続き使う）。
builder.Services.AddSingleton(TimeProvider.System);
// FR-01, FR-02, FR-13, #1015, IADR-0435: Finnhub の対象銘柄は**市場監視の監視銘柄**から決める（取引判断の定時サイクルが
// 判断対象を決めるのと同じ GET /monitor/watchlist・OwnerOrService。IADR-0095 と同じ s2s の作法＝trading-service のトークン・短いタイムアウト）。
// MarketMonitor:BaseUrl 未設定/不正 URI は従来どおり構成の固定リスト（Collection:Source:Finnhub:Symbols）。結線時の固定リストは
// **一度も読めていないときのフォールバックだけ**である（読めなければ直前に読めた対象を使い続ける＝不明を空と扱わない）。
// 1 巡回の要求は巡回間隔に収める（計画 ADR-0043 決定2 (b)。自制レート × 巡回間隔 ÷ 1 銘柄あたりの要求数）。
builder.Services.AddHttpClient("monitor", c => c.Timeout = TimeSpan.FromSeconds(5))
    .AddAiStockTradingServiceToken(builder.Configuration);
builder.Services.AddSingleton(sp =>
{
    var sourceOptions =
        builder.Configuration.GetSection(CollectionSourceOptions.SectionName).Get<CollectionSourceOptions>() ?? new();
    var pollIntervalSeconds =
        builder.Configuration.GetSection(CollectionOptions.SectionName).Get<CollectionOptions>()?.PollIntervalSeconds
            ?? new CollectionOptions().PollIntervalSeconds;

    IWatchlistReader? reader = null;
    var baseUrl = builder.Configuration["MarketMonitor:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
    {
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("monitor");
        http.BaseAddress = uri;
        reader = new HttpMarketMonitorWatchlistReader(http, sp.GetRequiredService<ILogger<HttpMarketMonitorWatchlistReader>>());
    }

    return new FinnhubSymbolSelector(
        reader,
        sourceOptions.Finnhub.Symbols,
        FinnhubCycleFit.MaxSymbolsPerCycle(
            sourceOptions.Finnhub.RateLimitPerMinute,
            pollIntervalSeconds,
            Math.Max(1, InformationSourceFactory.FinnhubRequestsPerSymbol(sourceOptions))),
        sp.GetRequiredService<BusinessMetrics>(),
        sp.GetRequiredService<ILogger<FinnhubSymbolSelector>>());
});

// #336, ADR-0020 決定3: 取得は**ソース単位の成否**を返す ISourceFetcher へ委ねる（どの区分が落ちたかを
// 欠測判定へ渡すため）。有効なソースが 0 件なら NoSourcesFetcher（外部接続しない安全既定）。
builder.Services.AddSingleton<ISourceFetcher>(sp =>
{
    var sourceOptions =
        builder.Configuration.GetSection(CollectionSourceOptions.SectionName).Get<CollectionSourceOptions>() ?? new();

    // #1015, IADR-0435: 銘柄の出所（監視銘柄への追随、または構成の固定リスト）があるときだけ選択器を渡す。
    // 渡さなければ従来どおり「固定リストが空なら Finnhub 系を有効化しない」。
    var finnhubSymbols = sp.GetRequiredService<FinnhubSymbolSelector>();
    var hasSymbolSource = finnhubSymbols.FollowsWatchlist
        || sourceOptions.Finnhub.Symbols.Any(s => !string.IsNullOrWhiteSpace(s));

    var sources = InformationSourceFactory.Create(
        sourceOptions,
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("collection"),
        sp.GetRequiredService<IClock>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILoggerFactory>(),
        hasSymbolSource ? finnhubSymbols : null);

    // FR-01, ADR-0031（計画）決定2〜4, IADR-0292: 情報収集ぶんの Finnhub 日次要求量の見積り
    // （銘柄未設定・Finnhub 系ソース未有効なら挙動中立）。巡回間隔は本サービス自身の CollectionOptions を使う。
    // ADR-0043（計画）決定 3, #1030, IADR-0437: 監視銘柄に追随するなら、起動時は 1 巡回の上限で数え、巡回ごとに実数で記録し直す（下の装飾）。
    var pollIntervalSeconds =
        builder.Configuration.GetSection(CollectionOptions.SectionName).Get<CollectionOptions>()?.PollIntervalSeconds
            ?? new CollectionOptions().PollIntervalSeconds;
    InformationSourceFactory.EvaluateDailyVolumeEstimate(
        sourceOptions,
        pollIntervalSeconds,
        FinnhubDailyVolumeGuardOptions.Read(builder.Configuration),
        sp.GetRequiredService<BusinessMetrics>(),
        sp.GetRequiredService<ILoggerFactory>(),
        finnhubSymbols.FollowsWatchlist ? InformationSourceFactory.FinnhubMaxSymbolsPerCycle(sourceOptions, pollIntervalSeconds) : null);

    if (sources.Count == 0)
        return new NoSourcesFetcher();

    ISourceFetcher runner = new SourceFetchRunner(sources, sp.GetRequiredService<ILogger<SourceFetchRunner>>());

    // #1015, IADR-0435: 監視銘柄に追随し、かつ Finnhub 系のソースが有効なら、取得の前に 1 回だけ対象銘柄を決め直す。
    var followsWatchlist = finnhubSymbols.FollowsWatchlist
        && sources.Any(s => s.Name is InformationSourceFactory.Finnhub or InformationSourceFactory.FinnhubNews);
    var metrics = sp.GetRequiredService<BusinessMetrics>();
    return followsWatchlist
        ? new WatchlistFollowingSourceFetcher(
            runner,
            finnhubSymbols,
            count => InformationSourceFactory.RecordCycleDailyVolumeEstimate(sourceOptions, pollIntervalSeconds, count, metrics))
        : runner;
});

// FR-01, FR-08, IADR-0069: KB 連携（保存 IKnowledgeBaseWriter・取得 IKnowledgeBaseSearch）を配線する。
// KnowledgeBase:Documents:BaseUrl 未設定なら保存は no-op、Search:BaseUrl 未設定なら取得は no-op（安全既定）。
// s2s トークンは ServiceAuth:ClientId/ClientSecret 設定時のみ付与（未設定は 401 → fail-safe）。
builder.Services.AddAiStockTradingKnowledgeBase(builder.Configuration);

// FR-01, FR-08, IADR-0069 決定 4: 収集情報の KB シンク。既定は no-op/ログ（LoggingKnowledgeBaseSink）を維持し、
// KnowledgeBase:Documents:BaseUrl 設定時のみ実 KB 保存（KnowledgeBaseWriterSink → IKnowledgeBaseWriter）へ切り替える。
// 選択は解決時に構成を読む（WebApplicationFactory の構成上書きに追随する）。
builder.Services.AddSingleton<LoggingKnowledgeBaseSink>();
builder.Services.AddSingleton<IKnowledgeBaseSink>(sp =>
{
    var baseUrl = sp.GetRequiredService<IConfiguration>()["KnowledgeBase:Documents:BaseUrl"];
    if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
        return sp.GetRequiredService<LoggingKnowledgeBaseSink>();

    return new KnowledgeBaseWriterSink(
        sp.GetRequiredService<IKnowledgeBaseWriter>(),
        sp.GetRequiredService<ILogger<KnowledgeBaseWriterSink>>());
});

// 収集オーケストレーション（scoped）。ポーリングは巡回ごとに DI スコープを作る。
builder.Services.AddScoped<AppSvc>();

// NFR（費用）, IADR-0031: 定時サイクルの費用統制（#23）ゲート。CostControl:BaseUrl 未設定/不正 URI は Placeholder
// （Normal＝停止も間隔延長もしない）＝安全既定でゲート。設定時のみ GET /costs/state を同期照会する（解決時に構成を読む）。
// 同期クリティカルパス（巡回冒頭）に置くため短いタイムアウトを設定する。
// IADR-0051: /costs/state は OwnerOrService のため client_credentials サービストークンを伝播する。
// ServiceAuth:ClientId/ClientSecret 未設定なら no-op（認証なし → 401 → Normal の安全既定）＝現行挙動を保持する。
builder.Services.AddHttpClient("cost", c => c.Timeout = TimeSpan.FromSeconds(5))
    .AddAiStockTradingServiceToken(builder.Configuration);
builder.Services.AddSingleton<PlaceholderCostControlGate>();
builder.Services.AddScoped<ICostControlGate>(sp =>
{
    var baseUrl = sp.GetRequiredService<IConfiguration>()["CostControl:BaseUrl"];
    if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        return sp.GetRequiredService<PlaceholderCostControlGate>();

    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("cost");
    http.BaseAddress = uri;
    return new HttpCostControlGate(http, sp.GetRequiredService<ILogger<HttpCostControlGate>>());
});

// ADR-0013, IADR-0129, #354: Wolverine（RabbitMQ）。ハンドラは持たず、収集完了時の InformationCollected 発行に用いる。
// キュー名・fan-out・再試行・DLQ の規則は共通ヘルパに閉じている（サービス側でトポロジを選ばない）。
builder.Host.UseWolverine(opts =>
    opts.UseAiStockTradingRabbitMq(ServiceName, builder.Configuration["RabbitMq:ConnectionString"]));

// 定時ポーリング（収集→保存→イベント発行）。#121: run-once エンドポイントから解決できるよう singleton 登録し、
// 同一インスタンスを HostedService としても起動する（Trigger=External では ExecuteAsync が巡回せず待機のみ）。
builder.Services.AddSingleton<CollectionPollingService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CollectionPollingService>());

var app = builder.Build();

// /health/live・/health/ready。
app.MapAiStockTradingHealthChecks();
app.MapAiStockTradingIntrospection();

// #121, FR-02, IADR-0023: 本番スケジューラ（K8s CronJob）からの run-once トリガ。1 巡回（RunOnceAsync）を起動する。
// in-process ポーリングと同じ処理で、休場日ガードは下流 TradeDecision（IADR-0023 の市場カレンダー）が担保する。
// 費用統制 Halted・収集ゼロは RunOnceAsync 内で no-op に倒れる（fail-safe）。
//
// NFR（セキュリティ）, #456, IADR-0176 決定1: **OwnerOrService**（`trading-owner` または `trading-service`）。
// 呼ぶ主体が CronJob（無人サービス）であるため OwnerOnly では成立しない。**渡しているのは「サイクルを起こす
// 権限」であって「発注する権限」ではない** —— 発注は下流の統制（費用停止・市場カレンダー・リスク評価・
// ブローカ階層の閂）を通る。IADR-0051 の「書き込みは OwnerOnly」に対する意図した例外であり、
// その非対称の理由は IADR-0176 決定1 に記録した。
app.MapPost("/internal/collection/run-once",
    async (CollectionPollingService poller, CancellationToken ct) =>
    {
        await poller.RunOnceAsync(ct);
        return Results.Ok();
    })
    .RequireAuthorization(AiStockTradingAuthPolicies.OwnerOrService);

// FR-01, FR-11, #336, ADR-0020 決定4: 一般インターネット収集（最終手段）の**発動申請**。
// 端点の本体は Features/InformationCollection/ActivateGeneralWebCollection/Endpoint.cs にある
// （MSP:ADR-0077 決定2・IADR-0289 決定1: 入口の配線はここに残し、操作の処理は 3 段目へ下ろす）。
app.MapActivateGeneralWebCollection();

// #811 / IADR-0129 追記: 全サービス共通の終端（shim）。JasperFx のコマンドライン（`dotnet <dll> codegen write` 等）を受け、引数なしは従来の app.Run と同じ稼働。
return await app.RunAiStockTradingAsync(args);

// FR-01, ADR-0043（計画）決定 3, #1030, IADR-0437: 起動時の日次要求の見積り（自己申告用）。監視銘柄に追随する構成
// （MarketMonitor:BaseUrl が絶対 URI）なら 1 巡回に収まる対象の数（上限）、そうでなければ構成の固定リストの数で数える。
static long EstimateAtStartup(IConfiguration configuration)
{
    var sourceOptions = configuration.GetSection(CollectionSourceOptions.SectionName).Get<CollectionSourceOptions>() ?? new();
    var pollIntervalSeconds = configuration.GetSection(CollectionOptions.SectionName).Get<CollectionOptions>()?.PollIntervalSeconds
        ?? new CollectionOptions().PollIntervalSeconds;
    var followsWatchlist = Uri.TryCreate(configuration["MarketMonitor:BaseUrl"], UriKind.Absolute, out _);
    return InformationSourceFactory.EstimateDailyVolume(
        sourceOptions,
        pollIntervalSeconds,
        followsWatchlist ? InformationSourceFactory.FinnhubMaxSymbolsPerCycle(sourceOptions, pollIntervalSeconds) : null);
}

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
