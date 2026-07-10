using AiStockTrading.CostControl.Application.Adapters;
using AiStockTrading.CostControl.Application.Ports;
using AiStockTrading.CostControl.Worker.Foundation.Endpoints;
using AiStockTrading.CostControl.Worker.Foundation.Persistence;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Serilog;
using AppSvc = AiStockTrading.CostControl.Application.Services.CostControlService;

const string ServiceName = "ai-stock-trading.cost-control-service";

// #23 Slice A, NFR（費用）, IADR-0027: 費用統制サービス。LLM の月次費用計上と上限に対する間隔延長/停止判定（Keycloak 認可）と
// ヘルスチェックのため WebApplication を用いる。しきい値の上方遷移時に CostThresholdReached を発行する（MassTransit）。
//
// IADR-0013: 本 Program.cs の standalone 配線は dev/test/CI のローカル単体実行のためのもの。本番は platform 統合（#22）で置換。
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((_, logConfig) =>
    logConfig.ConfigureAiStockTradingSerilog(builder.Configuration, ServiceName));
builder.Services.AddAiStockTradingObservability(builder.Configuration, ServiceName);

// ADR-0007: Keycloak 認証（費用計上・照会）。
builder.Services.AddAiStockTradingAuth(builder.Configuration);

// ADR-0001（Database per Service）, IADR-0027: 費用統制専有 DB（cost_control_svc）。
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=postgres;Port=5432;Database=cost_control_svc;Username=ai;Password=ai";
builder.Services.AddDbContext<CostControlDbContext>(opt => opt.UseNpgsql(connStr));

builder.Services.AddAiStockTradingHealthChecks()
    .AddNpgSql(connStr, tags: ["ready"]);

// 列挙（CostCategory/State）を文字列で送受信する。
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddSingleton<IClock, SystemClock>();
// 月次費用上限は暫定で前提条件の既定値（#19 のバージョン付き取得は #22 後続）。
builder.Services.AddSingleton<ICostLimitsProvider, DefaultCostLimitsProvider>();
builder.Services.AddScoped<ICostLedger, EfCostLedger>();
builder.Services.AddScoped<AppSvc>();

// IADR-0011/0027: MassTransit（RabbitMQ）。消費者は持たず、しきい値到達時の CostThresholdReached 発行に用いる。
builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((_, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672");
        cfg.UseAiStockTradingRetry();
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CostControlDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

app.UseAiStockTradingMiddleware();
app.MapAiStockTradingHealthChecks();

// NFR（費用）: 費用計上・統制判定・費用レビュー（利用者/サービス）。
app.MapCostControlEndpoints();

app.Run();

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
