using AiStockTrading.MarketMonitor.Application.Adapters;
using AiStockTrading.MarketMonitor.Application.Ports;
using AiStockTrading.MarketMonitor.Application.Services;
using AiStockTrading.MarketMonitor.Infrastructure.Composable.Adapters;
using AiStockTrading.MarketMonitor.Infrastructure.Composable.Polling;
using AiStockTrading.MarketMonitor.Infrastructure.Composable.Steps;
using AiStockTrading.MarketMonitor.Api.Foundation.Endpoints;
using AiStockTrading.MarketMonitor.Infrastructure.Foundation.Persistence;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Wolverine;

const string ServiceName = "ai-stock-trading.market-monitor-service";

// #10 Slice B, IADR-0013/0014: 監視設定変更の HTTP エンドポイント（Keycloak 認可）とヘルスチェックのため
// WebApplication を用いる。ポーリングは BackgroundService、TradeDecisionMade 購読は Wolverine のハンドラ。
//
// IADR-0013: 本 Program.cs の standalone 配線（Wolverine/RabbitMQ・PostgreSQL・Keycloak を
// AiStockTrading.TestSupport.PlatformShim 経由で組む部分）は dev/test/CI でのローカル単体実行のためのもの。
// 本番（実運用）では market-monitor は platform の可変部分へ組み込まれ、共通基盤は platform 本体の Foundation が
// 提供する（本番統合は #22）。取引ドメインの本番実装は Domain/Application と、本ホストの再利用可能部
// （MonitorPollingService・TradeDecisionMadeBaselineConsumer・EF ストア・エンドポイントハンドラ）である。
var builder = WebApplication.CreateBuilder(args);

// IADR-0011/0013: 可観測性（Serilog + OTel）。
builder.Services.AddSerilog((_, logConfig) =>
    logConfig.ConfigureAiStockTradingSerilog(builder.Configuration, ServiceName));
builder.Services.AddAiStockTradingObservability(builder.Configuration, ServiceName);

// ADR-0004（platform）: Keycloak 認証（監視設定変更は OwnerOnly）。
builder.Services.AddAiStockTradingAuth(builder.Configuration);

// ADR-0001（Database per Service）, IADR-0012 踏襲: 市場監視専有 DB（market_monitor_svc）。
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=postgres;Port=5432;Database=market_monitor_svc;Username=ai;Password=ai";
builder.Services.AddDbContext<MarketMonitorDbContext>(opt => opt.UseNpgsql(connStr));

builder.Services.AddAiStockTradingHealthChecks()
    .AddNpgSql(connStr, tags: ["ready"]);

// --- 市場監視のポートとサービス（Slice A）を配線する ---
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IMarketSchedule, WeekdayMarketSchedule>();
// FR-03, #158, IADR-0066/0068: 現在値ソースは構成 MarketData:Provider で選択（既定・空・未知は no-op＝実接続しない・
// 従来挙動）。finnhub 指定＋API キーありのときだけ実市況になる。構成は解決時に読む（起動時読み取りだと
// WebApplicationFactory の構成上書きに追随しないため。IPositionStore と同じ規約）。
// 監視の巡回には前回値フォールバック（LastKnownQuoteSource）を**かけない**: 前回値で損切りライン到達を判定すると、
// 市況断のあいだ古い価格から StopLossTriggered＝実際の決済発注が誤発火しうる。取得不可はスキップ（＝発注しない）が安全側。
// 前回値フォールバックは発注を伴わない時価評価（リスク管理・報告書）にのみ適用する（IADR-0066 決定 2）。
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient("marketdata");
builder.Services.AddSingleton<IMarketDataSource>(sp => MarketDataSourceFactory.Create(
    sp.GetRequiredService<IConfiguration>().GetSection(MarketDataOptions.SectionName).Get<MarketDataOptions>() ?? new(),
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("marketdata"),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILoggerFactory>()));
// FR-03/FR-10, IADR-0030: 保有ポジションはリスク管理（#12）の GET /risk-controls/open-positions を同期照会して供給する。
// RiskManagement:BaseUrl 未設定/不正 URI は従来プレースホルダ（保有なし＝損切り検知対象なし）＝安全既定でゲート。
// 選択は解決時に構成を読む（起動時読み取りだと WebApplicationFactory の構成上書きに追随しないため）。HttpClient は
// IHttpClientFactory 経由でハンドラをプールする。損切り優先の巡回を長時間ブロックしないため短いタイムアウトを設定する。
// IADR-0051: OwnerOrService エンドポイント（open-positions）へ client_credentials サービストークンを伝播する。
// ServiceAuth:ClientId/ClientSecret 未設定なら no-op（認証なし → 401 → 空列の安全既定）＝現行挙動を保持する。
builder.Services.AddHttpClient("risk", c => c.Timeout = TimeSpan.FromSeconds(5))
    .AddAiStockTradingServiceToken(builder.Configuration);
builder.Services.AddSingleton<PlaceholderPositionStore>();
builder.Services.AddScoped<IPositionStore>(sp =>
{
    var baseUrl = sp.GetRequiredService<IConfiguration>()["RiskManagement:BaseUrl"];
    if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        return sp.GetRequiredService<PlaceholderPositionStore>();

    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("risk");
    http.BaseAddress = uri;
    return new HttpPositionStore(http, sp.GetRequiredService<ILogger<HttpPositionStore>>());
});
// DbContext が scoped のため設定/基準値/クールダウンの EF ストアも scoped。
builder.Services.AddScoped<IMonitoredSymbolStore, EfMonitoredSymbolStore>();
builder.Services.AddScoped<IPriceBaselineStore, EfPriceBaselineStore>();
builder.Services.AddScoped<ICooldownStore, EfCooldownStore>();
builder.Services.AddScoped<MarketMonitorService>();

// FR-03/FR-11/FR-13, UC-06, IADR-0088: 監視銘柄（watchlist）の取得/追加/削除と変更履歴（Risk 設定の作法をミラー）。
// IClock は上で singleton 登録済み。変更履歴は DbContext 依存のため scoped。
builder.Services.AddScoped<IMonitorSettingsChangeLog, EfMonitorSettingsChangeLog>();
builder.Services.AddScoped<MonitorWatchlistService>();

// FR-03: ポーリング構成（監視間隔）。
builder.Services.Configure<MonitorOptions>(builder.Configuration.GetSection(MonitorOptions.SectionName));
// FR-03: 監視間隔ごとのポーリング（市場開場時に評価・発行）。
builder.Services.AddHostedService<MonitorPollingService>();

// ADR-0013, IADR-0129, #354: Wolverine（RabbitMQ）。基準値更新のため TradeDecisionMade を購読、監視イベントを発行する。
// ハンドラは明示登録ではなくアセンブリ走査で発見されるため、ハンドラを持つアセンブリ（Infrastructure）を明示する。
// キュー名・fan-out・再試行・DLQ の規則は共通ヘルパに閉じている（サービス側でトポロジを選ばない）。
builder.Host.UseWolverine(opts => opts.UseAiStockTradingRabbitMq(
    ServiceName,
    builder.Configuration["RabbitMq:ConnectionString"],
    typeof(TradeDecisionMadeBaselineHandler).Assembly));

// ADR-0001, FR-15, #22 受け入れ基準③: 実効構成（有効な段=宣言由来・選択中ポート実装・構成バージョン）の自己申告。
// メッシュ内部限定エンドポイント GET /internal/introspection（無認可・ネットワーク分離が防御）。
builder.Services.AddAiStockTradingIntrospection(builder.Configuration, ServiceName, b => b
    .AddPort("market-data", string.IsNullOrWhiteSpace(builder.Configuration["MarketData:Provider"]) ? "noop" : builder.Configuration["MarketData:Provider"]!)
    .AddPortFromBaseUrl("position-store", builder.Configuration["RiskManagement:BaseUrl"], "http", "placeholder"));

var app = builder.Build();

// IADR-0012 踏襲: 起動時にスキーマを最新 Migration へ更新（relational のみ。テストの InMemory はスキップ）。
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MarketMonitorDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

app.UseAiStockTradingMiddleware();
app.MapAiStockTradingHealthChecks();
app.MapAiStockTradingIntrospection();

// FR-03, FR-13: 監視設定の照会・変更（利用者のみ）。
app.MapMonitorSettingsEndpoints();

app.Run();

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
