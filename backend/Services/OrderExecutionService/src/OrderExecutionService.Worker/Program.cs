using AiStockTrading.OrderExecution.Application.Adapters;
using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.OrderExecution.Application.Services;
using AiStockTrading.OrderExecution.Worker.Composable.Adapters;
using AiStockTrading.OrderExecution.Worker.Composable.Retention;
using AiStockTrading.OrderExecution.Worker.Composable.Steps;
using AiStockTrading.OrderExecution.Worker.Foundation.Persistence;
using AiStockTrading.Shared.Contracts.Operations;
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
// #131, IADR-0057: 発注前 DecisionId 予約（二重発注の防止）。
builder.Services.AddScoped<IOrderReservationStore, EfOrderReservationStore>();
builder.Services.AddScoped<OrderExecutionService>();

// #154, FR-19, IADR-0067: 注文履歴テレメトリ（訂正・取消の適用＋永続化＋発行）。
// 訂正・取消の口（IOrderAmendmentBroker）はペーパーだけが実装する。実ブローカー（moomoo）選択時は本経路を
// 登録しない＝実弾に対する訂正・取消が構成上も存在しない（fail-safe）。実ブローカーの訂正・取消配線は
// 後続・実コンテナ E2E（#82 系）で扱う。
// 駆動元（時限取消・#141 リコンサイル基点・#152 pause 強制取消）は本 PR の対象外で、それらが
// OrderAmendmentDispatcher を呼ぶ。moomoo 構成でそれらを配線した場合は DI 解決に失敗して起動時に気づける。
builder.Services.AddScoped<IOrderLifecycleStore, EfOrderLifecycleStore>();
if (!BrokerFactory.IsMoomoo(builder.Configuration["Broker:Provider"]))
{
    builder.Services.AddSingleton<IOrderAmendmentBroker>(sp =>
        (IOrderAmendmentBroker)sp.GetRequiredService<IBrokerAdapter>());
    builder.Services.AddScoped<OrderAmendmentService>();
    builder.Services.AddScoped<OrderAmendmentDispatcher>();
}

// NFR（運用）, #137, IADR-0059: 予約表の終端行（Completed）の保持期間パージ（既定無効。Retention:Enabled=true で有効化）。
// Reserved（＝発注済みか不明）はどれだけ古くても対象外。滞留の解消は #141 か人手であって時間経過ではない。
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection(RetentionOptions.SectionName));
builder.Services.AddHostedService<OrderReservationRetentionService>();

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
