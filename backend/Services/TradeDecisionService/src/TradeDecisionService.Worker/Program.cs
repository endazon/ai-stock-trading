using AiStockTrading.Configuration.Client.Foundation.Extensions;
using AiStockTrading.Configuration.Client.Ports;
using AiStockTrading.TradeDecision.Application.Adapters;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Application.Services;
using AiStockTrading.TradeDecision.Worker.Composable.Adapters;
using AiStockTrading.TradeDecision.Worker.Composable.Steps;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.KnowledgeBase.Foundation.Extensions;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;
using MassTransit;
using Serilog;
using System.Globalization;

const string ServiceName = "ai-stock-trading.trade-decision-service";

// #11 Slice A / #21 (FR-02), IADR-0013/0017/0023: ヘルスチェックの HTTP サーフェスのため WebApplication を用いる。
// 価格変動（PriceMovementDetected・イベント駆動）と収集完了（InformationCollected・定時）の両系統を MassTransit
// コンシューマとして購読し、市場カレンダー（IMarketCalendar）で休場日をゲートしつつ取引判断で合流する。判断はステートレス（DB なし）。
//
// IADR-0013: 本 Program.cs の standalone 配線（MassTransit/RabbitMQ を shim 経由で組む部分）は dev/test/CI の
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
    .AddPortFromBaseUrl("assumptions", builder.Configuration["Configuration:BaseUrl"], "http", "placeholder"));

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
// 単価は LlmPricing:InputPer1kTokens / OutputPer1kTokens（円・既定 0）。fail-safe: 未設定=0 円＝統制判定に影響しない。
// 金額 0 でも publish して計上経路の健全性を保つ（IADR-0055 根拠）。ポートの安全既定は NoOpLlmUsageReporter。
builder.Services.AddScoped<ILlmUsageReporter>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    return new PublishingLlmUsageReporter(
        sp.GetRequiredService<IPublishEndpoint>(),
        sp.GetRequiredService<IClock>(),
        ParsePricePer1k(cfg["LlmPricing:InputPer1kTokens"]),
        ParsePricePer1k(cfg["LlmPricing:OutputPer1kTokens"]),
        sp.GetRequiredService<ILogger<PublishingLlmUsageReporter>>());
});

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
        cfg["LlmGateway:Purpose"] ?? "trade-decision",
        sp.GetRequiredService<ILlmUsageReporter>(),
        // FR-11, IADR-0061 決定1: 全量ログ（プロンプト・生出力）。既定オフ＝機微を既定でログ基盤へ流さない。
        logPrompts: bool.TryParse(cfg["LlmGateway:LogPrompts"], out var logPrompts) && logPrompts);
});

// #11, IADR-0061 決定2: LLM ゲートウェイのタイムアウト（秒）。未設定・不正・非正値は既定 30 秒（fail-safe）。
static TimeSpan ParseTimeout(string? value) =>
    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
        ? TimeSpan.FromSeconds(seconds)
        : TimeSpan.FromSeconds(30);

// 単価の構成読み取り（円/1k トークン）。未設定・不正・負値は 0（fail-safe）。
static decimal ParsePricePer1k(string? value) =>
    decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) && price > 0m
        ? price
        : 0m;

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

// FR-02, IADR-0023: 市場カレンダー（休場日ゲート）と定時サイクルの監視銘柄（暫定=構成ベース）。
builder.Services.AddSingleton<IMarketCalendar>(_ => new MarketCalendar(LoadHolidays(builder.Configuration)));
builder.Services.AddSingleton<IWatchlistProvider, ConfigurationWatchlistProvider>();
// FR-04, IADR-0039: 多数決・二段オーケストレーションの構成（Decision:*）。未設定なら Default（1 票・スクリーニング無効）
// ＝単発判断（IADR-0017）と等価＝現行挙動。実 LLM/モデル解決・回数の実値は後続（#23/#79 と連動）。
builder.Services.AddSingleton(DecisionOptionsLoader.FromConfiguration(builder.Configuration));

// FR-17, 05_trading-assumptions §4, IADR-0076: 採算評価ゲート（Profitability:*）。未設定なら Default（無効＝現行挙動）。
// 有効時は往復概算費用に対する最小期待利益を評価し、採算不成立・費用見積り不能は Hold に倒す。
builder.Services.AddSingleton(ProfitabilityGateOptionsLoader.FromConfiguration(builder.Configuration));
// FR-17, IADR-0063/0076: 版付き全体前提条件の解決を配線（Configuration:BaseUrl 未設定なら既定＝未解決＝採算見積り不能）。
// #139（CostControl）と同一の共有クライアントで、キャッシュ・AssumptionsChanged 無効化・fail-safe を委ねる（二重キャッシュを作らない）。
builder.Services.AddAiStockTradingAssumptions(builder.Configuration);
// FR-17, IADR-0076: 採算費用見積りのアダプタ。前提条件の解決可否は上の IAssumptionsProvider（Configuration:BaseUrl）で決まる。
builder.Services.AddScoped<IProfitabilityAssumptionsProvider>(sp =>
    new AssumptionsProfitabilityProvider(sp.GetRequiredService<IAssumptionsProvider>()));

builder.Services.AddScoped<TradeDecisionService>();

// ADR-0003, IADR-0011, IADR-0023: MassTransit（RabbitMQ）。価格変動（イベント駆動）と収集完了（定時）の両系統を購読し、
// 取引判断で合流して TradeDecisionMade を発行する。
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<PriceMovementDetectedConsumer>();
    x.AddConsumer<InformationCollectedConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672");
        cfg.UseAiStockTradingRetry();
        cfg.ConfigureEndpoints(ctx);
    });
});

var app = builder.Build();

app.MapAiStockTradingHealthChecks();
app.MapAiStockTradingIntrospection();

app.Run();

// IADR-0023: 市場別の休場日を構成（TradeCycle:Holidays:<Market> = ["yyyy-MM-dd", ...]）から読み込む。既定は空（週末のみ）。
static IReadOnlyDictionary<Market, IReadOnlySet<DateOnly>> LoadHolidays(IConfiguration configuration)
{
    var result = new Dictionary<Market, IReadOnlySet<DateOnly>>();
    foreach (var market in Enum.GetValues<Market>())
    {
        var dates = configuration.GetSection($"TradeCycle:Holidays:{market}").Get<string[]>() ?? [];
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
