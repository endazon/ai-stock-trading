using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Infrastructure.ExternalServices;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Fx;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AiStockTrading.Shared.KnowledgeBase.Foundation.Extensions;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using Microsoft.Extensions.Options;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;
using Serilog;
using Wolverine;
using System.Globalization;

const string ServiceName = "ai-stock-trading.trade-decision-service";

// #11 Slice A / #21 (FR-02), IADR-0013/0017/0023: ヘルスチェックの HTTP サーフェスのため WebApplication を用いる。
// 価格変動（PriceMovementDetected・イベント駆動）と収集完了（InformationCollected・定時）の両系統を Wolverine
// コンシューマとして購読し、市場カレンダー（IMarketCalendar）で休場日をゲートしつつ取引判断で合流する。判断はステートレス（DB なし）。
//
// IADR-0013: 本 Program.cs の standalone 配線（Wolverine/RabbitMQ を shim 経由で組む部分）は dev/test/CI の
// ローカル単体実行のためのもの。本番は platform 統合（#22）で共通基盤に置き換わる。
// IADR-0017: 実 LLM/実データはプレースホルダ（安全既定＝取引しない）。実 LLM（platform /complete）・実データは後続。
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((_, logConfig) =>
    logConfig.ConfigureAiStockTradingSerilog(builder.Configuration, ServiceName));
builder.Services.AddAiStockTradingObservability(builder.Configuration, ServiceName);

builder.Services.AddAiStockTradingHealthChecks();
// ADR-0001, FR-15, #22 受け入れ基準③: 実効構成（有効な段=宣言由来・選択中ポート実装・構成バージョン）の自己申告。
// メッシュ内部限定エンドポイント GET /internal/introspection（無認可・ネットワーク分離が防御）。
builder.Services.AddAiStockTradingIntrospection(builder.Configuration, ServiceName, b => b
    .AddPortFromBaseUrl("llm-completion", builder.Configuration["LlmGateway:BaseUrl"], "http", "placeholder")
    .AddPortFromBaseUrl("daily-policy", builder.Configuration["Reports:BaseUrl"], "http", "placeholder")
    .AddPortFromBaseUrl("sizing-context", builder.Configuration["RiskManagement:BaseUrl"], "http", "placeholder")
    .AddPortFromBaseUrl("knowledge-base-search", builder.Configuration["KnowledgeBase:Search:BaseUrl"], "http", "noop")
    .AddPortFromBaseUrl("assumptions", builder.Configuration["Configuration:BaseUrl"], "http", "placeholder")
    // FR-02, IADR-0095/0078 決定4: 監視銘柄（watchlist）供給の選択中実装を自己申告する。MarketMonitor:BaseUrl 設定時=http
    // （権威源 GET /monitor/watchlist へ s2s 照会）、未設定/不正=configuration（構成フォールバック）。introspection から結線状態を判別可能にする。
    .AddPortFromBaseUrl("watchlist", builder.Configuration["MarketMonitor:BaseUrl"], "http", "configuration")
    // FR-02, #158, IADR-0068/0099: 判断文脈の現在値ソースの選択中実装を自己申告する。MarketData:Provider 設定時=その値
    //（finnhub 等）、未設定=noop（現在値なし＝現行挙動）。introspection から現在値供給の結線状態を判別可能にする。
    .AddPort("market-data", string.IsNullOrWhiteSpace(builder.Configuration["MarketData:Provider"]) ? "noop" : builder.Configuration["MarketData:Provider"]!)
    // FR-10, FR-17, #257, IADR-0107: 基準通貨への換算レート源の選択中実装を自己申告する。解決規則は
    // FxRateSourceFactory.ResolveProvider を単一情報源にし（構成不備で no-op へ倒れる場合は none）、申告と実体をずらさない。
    .AddPort("fx-rate", FxRateSourceFactory.ResolveProvider(
        builder.Configuration.GetSection(FxOptions.SectionName).Get<FxOptions>() ?? new FxOptions())));

// --- 取引判断のポートとサービス（Slice A）を配線する ---
builder.Services.AddSingleton<IClock, SystemClock>();
// #79, FR-04, IADR-0017/0039: 実 LLM は platform LLM ゲートウェイ（POST /complete）へ委譲する。
// LlmGateway:BaseUrl 未設定/不正 URI は従来プレースホルダ（常に Hold・取引しない）＝安全既定でゲート。設定時のみ実照会する。
// 選択は解決時に構成を読む（起動時読み取りだと WebApplicationFactory の構成上書きに追随しないため）。LLM は応答が遅い
// ため HttpClient のタイムアウトは長め。送信拒否/失敗/タイムアウトは Hold に倒す（HttpLlmCompletionClient）。
// #11, IADR-0061 決定2: タイムアウトは LlmGateway:TimeoutSeconds（秒）。実運用ではモデル・プロンプト長で適正値が変わる。
// fail-safe: 未設定・不正・非正値は既定 30 秒（＝従来値）へ倒す（無限待ちや 0 秒にはしない）。
// IADR-0061 決定3/4: /complete は匿名エンドポイント（platform 側に RequireAuthorization/FallbackPolicy なし）のため
// s2s トークンは付けない。リトライはゲートウェイ側が一元化する（ADR-0010）ため呼び出し側では重ねない。
builder.Services.AddHttpClient("llm", c => c.Timeout = ParseTimeout(builder.Configuration["LlmGateway:TimeoutSeconds"]));
builder.Services.AddSingleton<PlaceholderLlmCompletionClient>();

// #79, IADR-0055 決定2/3: LLM 費用計測。egress の成功応答トークンに単価を適用し LlmCostIncurred を publish する
// （費用統制サービスが購読して月次計上。HTTP /costs/record は OwnerOnly のため使わない）。
// #303, IADR-0122 決定2/3: 単価は**応答が名乗った実効モデル**で引く（用途別モデル割当でモデルが混在するため）。
// モデル別は LlmPricing:PerModel:<model-id>:InputPer1kTokens / OutputPer1kTokens（円/1k）。
// 未設定なら従来キー LlmPricing:InputPer1kTokens / OutputPer1kTokens（global 単一ペア）へ倒れる＝後方互換。
// 金額 0 でも publish して計上経路の健全性を保つ（IADR-0055 根拠）。ポートの安全既定は NoOpLlmUsageReporter。
// NFR（費用）, #347, IADR-0218: 用途（purpose）を必ず載せる。費用統制の対象範囲は購読側が purpose で判別する。
// #335, IADR-0212: 用途は**計測ごと**に egress（HttpLlmCompletionClient）が載せる。ここで固定すると
// 二段判断の一次スクリーニングと本判断が同じ用途で積まれ、層別の内訳が取れない。
builder.Services.AddScoped<ILlmUsageReporter>(sp => new PublishingLlmUsageReporter(
    sp.GetRequiredService<IMessageBus>(),
    sp.GetRequiredService<IClock>(),
    BuildLlmPriceTable(sp.GetRequiredService<IConfiguration>()),
    sp.GetRequiredService<ILogger<PublishingLlmUsageReporter>>()));

// FR-04, FR-09, FR-11, ADR-0017 決定2/決定4, #335, IADR-0216/0217: 割当統制の可観測性。
// フォールバック発火（LlmFallbackFired）と取引判断の見送り（TradeDecisionSkipped）を publish する。
builder.Services.AddScoped<ILlmGovernanceReporter>(sp => new PublishingLlmGovernanceReporter(
    sp.GetRequiredService<IMessageBus>(),
    sp.GetRequiredService<IClock>(),
    sp.GetRequiredService<ILogger<PublishingLlmGovernanceReporter>>()));

builder.Services.AddScoped<ILlmCompletionClient>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var baseUrl = cfg["LlmGateway:BaseUrl"];
    // 未設定・不正 URI は安全既定（プレースホルダ＝常に Hold・取引しない）に倒す。
    if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        return sp.GetRequiredService<PlaceholderLlmCompletionClient>();

    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("llm");
    http.BaseAddress = uri;
    return new HttpLlmCompletionClient(http,
        sp.GetRequiredService<ILogger<HttpLlmCompletionClient>>(),
        cfg["LlmGateway:Confidentiality"] ?? "internal",
        // #335, IADR-0212: 用途は**呼び出しごと**に DecisionOrchestrator が名乗る（一次=trade-decision-screening／
        // 二次=trade-decision）。ここを固定すると層を区別できず、一次の応答が二次の割当と照合されて全サイクルが
        // 見送りへ倒れる。LlmGateway:Purpose は明示設定時のみ全呼び出しへ上書き適用する（既存デプロイの非破壊）。
        cfg["LlmGateway:Purpose"],
        sp.GetRequiredService<ILlmUsageReporter>(),
        // FR-11, IADR-0061 決定1: 全量ログ（プロンプト・生出力）。既定オフ＝機微を既定でログ基盤へ流さない。
        logPrompts: bool.TryParse(cfg["LlmGateway:LogPrompts"], out var logPrompts) && logPrompts,
        sp.GetRequiredService<ILlmGovernanceReporter>());
});

// #11, IADR-0061 決定2: LLM ゲートウェイのタイムアウト（秒）。未設定・不正・非正値は既定 30 秒（fail-safe）。
static TimeSpan ParseTimeout(string? value) =>
    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
        ? TimeSpan.FromSeconds(seconds)
        : TimeSpan.FromSeconds(30);

// #303, IADR-0122 決定2: モデル別単価表を構成から組み立てる。単価の解析（InvariantCulture）と fail-safe
//（未知モデル＝表の最大単価 / 表が空＝従来キー / 何も無ければ 0）は LlmPriceTable に閉じている。
static LlmPriceTable BuildLlmPriceTable(IConfiguration cfg) =>
    LlmPriceTable.From(
        cfg.GetSection("LlmPricing:PerModel").GetChildren()
            .Select(m => (Model: m.Key, Input: m["InputPer1kTokens"], Output: m["OutputPer1kTokens"])),
        cfg["LlmPricing:InputPer1kTokens"],
        cfg["LlmPricing:OutputPer1kTokens"]);

// FR-08, IADR-0072 決定5: RAG 取得件数（TopK）。未設定・不正・非正値は既定 5（fail-safe）。
static int ParseTopK(string? value) =>
    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var topK) && topK > 0
        ? topK
        : 5;
// FR-07, IADR-0028: 確定済み日報方針は報告書サービス（#14）の GET /reports/daily-policy を同期照会して供給する。
// Reports:BaseUrl 未設定なら従来のプレースホルダ（no-op・取引しない）＝安全既定でゲート。設定時のみ実照会する。
// 選択は解決時に構成を読む（起動時読み取りだと WebApplicationFactory の構成上書きに追随しないため）。HttpClient は
// IHttpClientFactory 経由でハンドラをプールする。警告の 1 回化のためプレースホルダは singleton で共有する。
// 同期クリティカルパス（取引判断）に置くため短いタイムアウトを設定する（応答遅延でサイクルを長時間ブロックしない）。
// IADR-0051: OwnerOrService エンドポイント（daily-policy）へ client_credentials サービストークンを伝播する。
// ServiceAuth:ClientId/ClientSecret 未設定なら no-op（認証なし → 401 → 安全既定）＝現行挙動を保持する。
builder.Services.AddHttpClient("reports", c => c.Timeout = TimeSpan.FromSeconds(5))
    .AddAiStockTradingServiceToken(builder.Configuration);
builder.Services.AddSingleton<PlaceholderDailyPolicyProvider>();
builder.Services.AddScoped<IDailyPolicyProvider>(sp =>
{
    var baseUrl = sp.GetRequiredService<IConfiguration>()["Reports:BaseUrl"];
    // 未設定・不正 URI は安全既定（プレースホルダ＝取引しない）に倒す。
    if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        return sp.GetRequiredService<PlaceholderDailyPolicyProvider>();

    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("reports");
    http.BaseAddress = uri;
    return new HttpDailyPolicyProvider(http, sp.GetRequiredService<ILogger<HttpDailyPolicyProvider>>());
});
// FR-04/10, IADR-0029: サイジング文脈はリスク管理（#12）の GET /risk-controls/sizing-context を同期照会して供給する。
// RiskManagement:BaseUrl 未設定/不正 URI は従来プレースホルダ（既定値）＝安全既定でゲート。選択は解決時に構成を読む。
builder.Services.AddHttpClient("risk", c => c.Timeout = TimeSpan.FromSeconds(5))
    .AddAiStockTradingServiceToken(builder.Configuration);
builder.Services.AddSingleton<PlaceholderSizingContextProvider>();
builder.Services.AddScoped<ISizingContextProvider>(sp =>
{
    var baseUrl = sp.GetRequiredService<IConfiguration>()["RiskManagement:BaseUrl"];
    if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        return sp.GetRequiredService<PlaceholderSizingContextProvider>();

    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("risk");
    http.BaseAddress = uri;
    return new HttpSizingContextProvider(http, sp.GetRequiredService<ILogger<HttpSizingContextProvider>>());
});
// FR-04/05/10, #292, IADR-0119: 判断由来の決済（AI の出口）に用いる保有建玉。リスク管理の既存
// GET /risk-controls/open-positions を同期照会する（新規エンドポイントは作らない・s2s トークンは "risk" クライアント）。
// RiskManagement:BaseUrl 未設定/不正 URI は NoOp（常に不明）＝売り判断は見送りへ倒れ、裸の新規売りを出さない。
builder.Services.AddSingleton<NoOpHeldPositionProvider>();
builder.Services.AddScoped<IHeldPositionProvider>(sp =>
{
    var baseUrl = sp.GetRequiredService<IConfiguration>()["RiskManagement:BaseUrl"];
    if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        return sp.GetRequiredService<NoOpHeldPositionProvider>();

    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("risk");
    http.BaseAddress = uri;
    return new HttpHeldPositionProvider(http, sp.GetRequiredService<ILogger<HttpHeldPositionProvider>>());
});
// FR-08, IADR-0069/0072: RAG 取得ポート（#18 IKnowledgeBaseSearch）を配線する。KnowledgeBase:Search:BaseUrl 未設定/不正なら
// #18 の NoOpKnowledgeBaseSearch（空）＝参考情報なし＝実 LLM 結線（IADR-0061）と同一プロンプト＝現行動作（安全既定）。
builder.Services.AddAiStockTradingKnowledgeBase(builder.Configuration);
// FR-08, IADR-0072: 判断文脈への RAG 取得アダプタ。常に登録し、実接続の可否は上の IKnowledgeBaseSearch（Search:BaseUrl）で決まる。
// TopK は Retrieval:TopK（既定 5・不正/非正値は既定へ）。取得失敗・空は判断側で「文脈なし」に縮退する（TradeDecisionService）。
builder.Services.AddScoped<IRetrievalContextProvider>(sp =>
    new KnowledgeBaseRetrievalContextProvider(
        sp.GetRequiredService<IKnowledgeBaseSearch>(),
        ParseTopK(sp.GetRequiredService<IConfiguration>()["Retrieval:TopK"]),
        sp.GetRequiredService<ILogger<KnowledgeBaseRetrievalContextProvider>>()));

// FR-02, IADR-0023, #337, IADR-0245: 市場カレンダー（休場日・半日取引日・場中ゲート）と定時サイクルの監視銘柄。
builder.Services.AddSingleton<IMarketCalendar>(_ => new MarketCalendar(
    LoadMarketDates(builder.Configuration, "TradeCycle:Holidays"),
    LoadMarketDates(builder.Configuration, "TradeCycle:HalfDays")));
// FR-02/13, UC-06, SC-02, IADR-0088/0095: 監視銘柄（watchlist）は権威源（市場監視 #10）の GET /monitor/watchlist を
// s2s 同期照会（OwnerOrService・IADR-0051）して供給する。MarketMonitor:BaseUrl 未設定/不正 URI は従来どおり構成ベース
// （TradeCycle:Watchlist）＝現行挙動・後方互換。照会失敗（非 2xx・timeout・例外）は構成ベース（既定 watchlist）へ倒す fail-safe。
builder.Services.AddHttpClient("monitor", c => c.Timeout = TimeSpan.FromSeconds(5))
    .AddAiStockTradingServiceToken(builder.Configuration);
builder.Services.AddSingleton<ConfigurationWatchlistProvider>();
builder.Services.AddScoped<IWatchlistProvider>(sp =>
{
    var configFallback = sp.GetRequiredService<ConfigurationWatchlistProvider>();
    var baseUrl = sp.GetRequiredService<IConfiguration>()["MarketMonitor:BaseUrl"];
    if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        return configFallback;

    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("monitor");
    http.BaseAddress = uri;
    return new HttpWatchlistProvider(http, configFallback, sp.GetRequiredService<ILogger<HttpWatchlistProvider>>());
});
// FR-04, IADR-0039, IADR-0212, IADR-0278, #571: 多数決・二段オーケストレーションの構成（Decision:*）。
// 未設定なら VoteCount=1・EnableScreening=true（#571 で基盤 trade-decision-screening 登録を前提に既定反転）。
// 明示的に Decision:EnableScreening=false を与えれば従来どおり単発判断（IADR-0017）へ戻せる。
builder.Services.AddSingleton(DecisionOptionsLoader.FromConfiguration(builder.Configuration));
// FR-02, FR-04, FR-06, FR-11, #337, IADR-0247: スクリーニング入力の縮退（Decision:ScreeningContextBudgetChars
// 設定時のみ発火）の記録経路。発生時に ScreeningContextReduced を publish し、監査台帳（月報の件数集計の
// 集計経路）へ届ける。予算未設定（既定）では縮退自体が起きないため publish は発生しない。
builder.Services.AddScoped<IScreeningReductionReporter, PublishingScreeningReductionReporter>();

// FR-17, 05_trading-assumptions §4, IADR-0076: 採算評価ゲート（Profitability:*）。未設定なら Default（無効＝現行挙動）。
// 有効時は往復概算費用に対する最小期待利益を評価し、採算不成立・費用見積り不能は Hold に倒す。
builder.Services.AddSingleton(ProfitabilityGateOptionsLoader.FromConfiguration(builder.Configuration));
// FR-17, IADR-0063/0076: 版付き全体前提条件の解決を配線（Configuration:BaseUrl 未設定なら既定＝未解決＝採算見積り不能）。
// #139（CostControl）と同一の共有クライアントで、キャッシュ・AssumptionsChanged 無効化・fail-safe を委ねる（二重キャッシュを作らない）。
builder.Services.AddAiStockTradingAssumptions(builder.Configuration);
// FR-17, IADR-0076: 採算費用見積りのアダプタ。前提条件の解決可否は上の IAssumptionsProvider（Configuration:BaseUrl）で決まる。
builder.Services.AddScoped<IProfitabilityAssumptionsProvider>(sp =>
    new AssumptionsProfitabilityProvider(sp.GetRequiredService<IAssumptionsProvider>()));

// UC-01, FR-09, FR-11, IADR-0096, #210: 日報未確定（policy-null）による見送りの通知。既定は NoOp（何もしない＝現行の
// ログのみ）。TradeCycle:NotifyOnUnconfirmedPolicy=true のときだけ実発行（DailyPolicyUnconfirmed の publish・営業日 dedup）へ
// 差し替える。既定・CI・未結線（Placeholder が常に null を返す）状態では publish しない＝現行挙動を完全維持する。実 Reports が
// 結線され null が真に「本日未確定」を意味する環境でのみ有効化する想定（IADR-0096 決定2）。singleton＝dedup 状態をプロセス内で共有。
if (bool.TryParse(builder.Configuration["TradeCycle:NotifyOnUnconfirmedPolicy"], out var notifyUnconfirmed) && notifyUnconfirmed)
    builder.Services.AddSingleton<IDailyPolicyUnconfirmedNotifier, PublishingDailyPolicyUnconfirmedNotifier>();
else
    builder.Services.AddSingleton<IDailyPolicyUnconfirmedNotifier, NoOpDailyPolicyUnconfirmedNotifier>();

// FR-02, FR-10, #158, IADR-0068/0099: 判断文脈の現在値（価格文脈）供給。現在値ソースは共有 MarketDataSourceFactory が
// MarketData:Provider で選ぶ（既定・空・未知・キー無しは no-op＝実接続しない＝現行挙動）。finnhub 指定＋API キーありの
// ときだけ実市況になる。ICurrentPriceProvider は鮮度（MaxQuoteStalenessSeconds・既定 300s）を検査し、取得不可・鮮度切れは
// 現在値なしへ倒す。IsEnabled は生成物が no-op でない（実結線）ときのみ true とし、判断側の fail-safe ゲート
// （有効化時のみ取得不可/鮮度切れを Hold）に用いる。定時サイクルでも現在値が供給され、権威価格でサイジングされる。
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<MarketDataOptions>(builder.Configuration.GetSection(MarketDataOptions.SectionName));
builder.Services.AddHttpClient("marketdata");
builder.Services.AddSingleton<IMarketDataSource>(sp => MarketDataSourceFactory.Create(
    sp.GetRequiredService<IOptions<MarketDataOptions>>().Value,
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("marketdata"),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddScoped<ICurrentPriceProvider>(sp =>
{
    var source = sp.GetRequiredService<IMarketDataSource>();
    var options = sp.GetRequiredService<IOptions<MarketDataOptions>>().Value;
    // 鮮度期限は共通概念（IADR-0066）。非正値は既定 300s（fail-safe）。
    var stalenessSeconds = options.MaxQuoteStalenessSeconds > 0 ? options.MaxQuoteStalenessSeconds : 300;
    // 実結線（no-op でない）ときのみ有効化＝有効化時に取得不可/鮮度切れを Hold へ倒すゲートを効かせる。
    return new MarketDataCurrentPriceProvider(
        source,
        enabled: source is not NoOpMarketDataSource,
        sp.GetRequiredService<IClock>(),
        TimeSpan.FromSeconds(stalenessSeconds),
        sp.GetRequiredService<ILogger<MarketDataCurrentPriceProvider>>());
});

// FR-10, FR-17, #257, #364, IADR-0107/0152: 基準通貨（USD）への換算レート供給。レート源は Fx:Provider で選ぶ
//（既定・空・未知・キー無しは no-op＝実接続しない）。基準通貨の市場（日本株）はレート 1 が定義から決まるため
// レート源に依存せず従来どおり判断でき、非基準通貨（米国株）はレートが解決できなければ新規建てを見送る（安全側）。
builder.Services.Configure<FxOptions>(builder.Configuration.GetSection(FxOptions.SectionName));
builder.Services.AddHttpClient("fx");

// FR-10, FR-17, FR-09, FR-11, #381, ADR-0022 決定2・決定5, IADR-0196: 為替の情報源の劣化を 3 経路へ可視化する。
// 監査・Discord は本ポートの publish が担い、日報は期間で引く（ReportService の供給ポート）。
// **singleton にする**——フォールバック中か・当日通知済みかの状態を巡回を跨いで保持するため
// （scoped だと巡回ごとに状態が消え、遷移が毎回「初回」になって洪水が戻る）。
builder.Services.AddSingleton<IFxSourceStatusNotifier, PublishingFxSourceStatusNotifier>();

builder.Services.AddSingleton<IFxRateSource>(sp => FxRateSourceFactory.Create(
    sp.GetRequiredService<IOptions<FxOptions>>().Value,
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("fx"),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILoggerFactory>(),
    sp.GetRequiredService<IFxSourceStatusNotifier>()));
builder.Services.AddScoped<IFxRateProvider>(sp => new MarketFxRateProvider(
    sp.GetRequiredService<IFxRateSource>(),
    sp.GetRequiredService<ILogger<MarketFxRateProvider>>()));

builder.Services.AddScoped<TradeDecisionAppService>();

// ADR-0003, IADR-0011, IADR-0023: 価格変動（イベント駆動）と収集完了（定時）の両系統を購読し、
// 取引判断で合流して TradeDecisionMade を発行する。
// ADR-0013, IADR-0129, #354: Wolverine（RabbitMQ）。ハンドラは明示登録ではなくアセンブリ走査で発見されるため、
// ハンドラを持つアセンブリ（Infrastructure）を明示する。キュー名・fan-out・再試行・DLQ の規則は共通ヘルパに閉じている。
builder.Host.UseWolverine(opts => opts.UseAiStockTradingRabbitMq(
    ServiceName,
    builder.Configuration["RabbitMq:ConnectionString"],
    typeof(PriceMovementDetectedHandler).Assembly));

var app = builder.Build();

app.MapAiStockTradingHealthChecks();
app.MapAiStockTradingIntrospection();

app.Run();

// IADR-0023, #337: 市場別の日付集合（休場日 TradeCycle:Holidays:<Market> / 半日取引日 TradeCycle:HalfDays:<Market>、
// いずれも ["yyyy-MM-dd", ...]）を構成から読み込む。既定は空（週末と場中時間帯のみ）。
static IReadOnlyDictionary<Market, IReadOnlySet<DateOnly>> LoadMarketDates(IConfiguration configuration, string sectionPrefix)
{
    var result = new Dictionary<Market, IReadOnlySet<DateOnly>>();
    foreach (var market in Enum.GetValues<Market>())
    {
        var dates = configuration.GetSection($"{sectionPrefix}:{market}").Get<string[]>() ?? [];
        var set = new HashSet<DateOnly>();
        foreach (var d in dates)
        {
            if (DateOnly.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                set.Add(date);
        }

        if (set.Count > 0)
            result[market] = set;
    }

    return result;
}

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
