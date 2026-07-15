using AiStockTrading.OrderExecution.Application.Adapters;
using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.OrderExecution.Application.Services;
using AiStockTrading.OrderExecution.Worker.Composable.Adapters;
using AiStockTrading.OrderExecution.Worker.Composable.Steps;
using AiStockTrading.OrderExecution.Worker.Foundation.Persistence;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Serilog;

const string ServiceName = "ai-stock-trading.order-execution-service";

// #13 Slice A, IADR-0013/0016: ヘルスチェックの HTTP サーフェスのため WebApplication を用いる。
// OrderApproved 購読は MassTransit コンシューマとして稼働する。
//
// IADR-0013: 本 Program.cs の standalone 配線（MassTransit/RabbitMQ・PostgreSQL を shim 経由で組む部分）は
// dev/test/CI でのローカル単体実行のためのもの。本番は platform 統合（#22）で共通基盤に置き換わる。
// IADR-0016: ブローカ既定はペーパー（実弾を撃たない）。moomoo は PoC まで構成ゲートで停止する。
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((_, logConfig) =>
    logConfig.ConfigureAiStockTradingSerilog(builder.Configuration, ServiceName));
builder.Services.AddAiStockTradingObservability(builder.Configuration, ServiceName);

// ADR-0001（Database per Service）: 発注執行専有 DB（order_execution_svc）。
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=postgres;Port=5432;Database=order_execution_svc;Username=ai;Password=ai";
builder.Services.AddDbContext<OrderExecutionDbContext>(opt => opt.UseNpgsql(connStr));

builder.Services.AddAiStockTradingHealthChecks()
    .AddNpgSql(connStr, tags: ["ready"]);

// IADR-0016, #13: ブローカ選択（構成 Broker:Provider・既定 paper）。moomoo/未知は起動時に安全停止（実弾防止）。
// moomoo 選択時は OpenD 接続クライアント（IMoomooTradeClient）を構成し SIMULATE 限定で発注する（実弾を撃たない）。
// DI ファクトリで組み、ロガーは DI の ILoggerFactory から取得する（クライアント/アダプタ双方に注入）。
builder.Services.AddSingleton<IBrokerAdapter>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var provider = cfg["Broker:Provider"];
    IMoomooTradeClient? moomooClient = BrokerFactory.IsMoomoo(provider)
        ? new MMApiMoomooTradeClient(
            MoomooBrokerOptions.FromConfiguration(cfg),
            loggerFactory.CreateLogger<MMApiMoomooTradeClient>())
        : null;
    return BrokerFactory.Create(provider, moomooClient, loggerFactory.CreateLogger<MoomooBrokerAdapter>());
});

builder.Services.AddSingleton<IClock, SystemClock>();
// DbContext が scoped のため発注結果ストアも scoped。
builder.Services.AddScoped<IExecutedOrderStore, EfExecutedOrderStore>();
builder.Services.AddScoped<OrderExecutionService>();

// ADR-0003, IADR-0011: MassTransit（RabbitMQ）。OrderApproved を購読し発注、OrderExecuted を発行する。
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<OrderApprovedConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672");
        cfg.UseAiStockTradingRetry();
        cfg.ConfigureEndpoints(ctx);
    });
});

var app = builder.Build();

// 起動時にスキーマを最新 Migration へ更新（relational のみ。テストの InMemory はスキップ）。
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrderExecutionDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

app.MapAiStockTradingHealthChecks();

app.Run();

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
