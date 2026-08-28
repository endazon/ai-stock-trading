using ConfigurationService.Application.Adapters;
using ConfigurationService.Application.Ports;
using ConfigurationService.Application.Services;
using ConfigurationService.Api.Endpoints;
using ConfigurationService.Infrastructure.Persistence;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Wolverine;

const string ServiceName = "ai-stock-trading.configuration-service";

// #19 Slice A, FR-17, IADR-0021: 設定管理サービス。全体前提条件のバージョン管理・変更履歴・利用者変更（Keycloak 認可）と
// ヘルスチェックのため WebApplication を用いる。更新時に AssumptionsChanged を発行する（Wolverine）。
//
// IADR-0013: 本 Program.cs の standalone 配線（Wolverine/RabbitMQ・PostgreSQL・Keycloak を shim 経由で組む部分）は
// dev/test/CI でのローカル単体実行のためのもの。本番は platform 統合（#22）で共通基盤に置き換わる。
var builder = WebApplication.CreateBuilder(args);

// IADR-0011: 可観測性（Serilog + OTel）。
builder.Services.AddSerilog((_, logConfig) =>
    logConfig.ConfigureAiStockTradingSerilog(builder.Configuration, ServiceName));
builder.Services.AddAiStockTradingObservability(builder.Configuration, ServiceName);

// ADR-0004（platform）: Keycloak 認証（前提条件の変更・照会は利用者のみ＝OwnerOnly）。
builder.Services.AddAiStockTradingAuth(builder.Configuration);

// ADR-0001（Database per Service）, IADR-0021: 設定管理専有 DB（configuration_svc）。
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=postgres;Port=5432;Database=configuration_svc;Username=ai;Password=ai";
builder.Services.AddDbContext<ConfigurationDbContext>(opt => opt.UseNpgsql(connStr));

builder.Services.AddAiStockTradingHealthChecks()
    .AddNpgSql(connStr, tags: ["ready"]);

// 時刻はステートレスのため singleton。DbContext が scoped のため EF ストアも scoped。
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<IAssumptionsStore, EfAssumptionsStore>();
builder.Services.AddScoped<IAssumptionsChangeLog, EfAssumptionsChangeLog>();
builder.Services.AddScoped<AssumptionsService>();

// ADR-0013, IADR-0129, #354: Wolverine（RabbitMQ）。ハンドラは持たず、更新時の AssumptionsChanged 発行に用いる。
// キュー名・fan-out・再試行・DLQ の規則は共通ヘルパに閉じている（サービス側でトポロジを選ばない）。
builder.Host.UseWolverine(opts =>
    opts.UseAiStockTradingRabbitMq(ServiceName, builder.Configuration["RabbitMq:ConnectionString"]));

// ADR-0001, FR-15, #22 受け入れ基準③: 実効構成（有効な段=宣言由来・選択中ポート実装・構成バージョン）の自己申告。
// メッシュ内部限定エンドポイント GET /internal/introspection（無認可・ネットワーク分離が防御）。
builder.Services.AddAiStockTradingIntrospection(builder.Configuration, ServiceName);

var app = builder.Build();

// IADR-0012 準拠: 起動時にスキーマを最新 Migration へ更新（relational のみ。テストの InMemory はスキップ）。
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ConfigurationDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

// 相関ID・認証・認可のミドルウェア。
app.UseAiStockTradingMiddleware();

// /health/live・/health/ready。
app.MapAiStockTradingHealthChecks();
app.MapAiStockTradingIntrospection();

// FR-17, UC-06: 前提条件の照会・変更（利用者のみ）。
app.MapAssumptionsEndpoints();

app.Run();

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
