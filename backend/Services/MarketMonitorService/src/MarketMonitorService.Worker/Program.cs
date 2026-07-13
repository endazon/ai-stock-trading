using AiStockTrading.MarketMonitor.Application.Adapters;
using AiStockTrading.MarketMonitor.Application.Ports;
using AiStockTrading.MarketMonitor.Application.Services;
using AiStockTrading.MarketMonitor.Worker.Composable.Adapters;
using AiStockTrading.MarketMonitor.Worker.Composable.Polling;
using AiStockTrading.MarketMonitor.Worker.Composable.Steps;
using AiStockTrading.MarketMonitor.Worker.Foundation.Endpoints;
using AiStockTrading.MarketMonitor.Worker.Foundation.Persistence;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Serilog;

const string ServiceName = "ai-stock-trading.market-monitor-service";

// #10 Slice B, IADR-0013/0014: 監視設定変更の HTTP エンドポイント（Keycloak 認可）とヘルスチェックのため
// WebApplication を用いる。ポーリングは BackgroundService、TradeDecisionMade 購読は MassTransit コンシューマ。
//
// IADR-0013: 本 Program.cs の standalone 配線（MassTransit/RabbitMQ・PostgreSQL・Keycloak を
// AiStockTrading.TestSupport.PlatformShim 経由で組む部分）は dev/test/CI でのローカル単体実行のためのもの。
// 本番（実運用）では market-monitor は platform の可変部分へ組み込まれ、共通基盤は platform 本体の Foundation が
// 提供する（本番統合は #22）。取引ドメインの本番実装は Domain/Application と、本ホストの再利用可能部
// （MonitorPollingService・TradeDecisionMadeConsumer・EF ストア・エンドポイントハンドラ）である。
var builder = WebApplication.CreateBuilder(args);

// IADR-0011/0013: 可観測性（Serilog + OTel）。
builder.Services.AddSerilog((_, logConfig) =>
    logConfig.ConfigureAiStockTradingSerilog(builder.Configuration, ServiceName));
builder.Services.AddAiStockTradingObservability(builder.Configuration, ServiceName);

// ADR-0004（platform）, ADR-0007: Keycloak 認証（監視設定変更は OwnerOnly）。
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
// FR-03: リアルタイム市況（moomoo・#13）が入るまではプレースホルダ（価格取得不可）。
builder.Services.AddSingleton<IMarketDataSource, PlaceholderMarketDataSource>();
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

// FR-03: ポーリング構成（監視間隔）。
builder.Services.Configure<MonitorOptions>(builder.Configuration.GetSection(MonitorOptions.SectionName));
// FR-03: 監視間隔ごとのポーリング（市場開場時に評価・発行）。
builder.Services.AddHostedService<MonitorPollingService>();

// ADR-0003, IADR-0011: MassTransit（RabbitMQ）。基準値更新のため TradeDecisionMade を購読、監視イベントを発行する。
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<TradeDecisionMadeConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672");
        cfg.UseAiStockTradingRetry();
        cfg.ConfigureEndpoints(ctx);
    });
});

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

// FR-03, FR-13, ADR-0007: 監視設定の照会・変更（利用者のみ）。
app.MapMonitorSettingsEndpoints();

app.Run();

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
