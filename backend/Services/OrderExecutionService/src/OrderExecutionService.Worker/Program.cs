using AiStockTrading.OrderExecution.Application.Adapters;
using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.OrderExecution.Application.Reconciliation;
using AiStockTrading.OrderExecution.Application.Services;
using AiStockTrading.OrderExecution.Worker.Composable.Adapters;
using AiStockTrading.OrderExecution.Worker.Composable.Reconciliation;
using AiStockTrading.OrderExecution.Worker.Composable.Retention;
using AiStockTrading.OrderExecution.Worker.Composable.Steps;
using AiStockTrading.OrderExecution.Worker.Foundation.Persistence;
using AiStockTrading.Shared.Contracts.Operations;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;
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

// IADR-0016, IADR-0111, #13: ブローカ選択（構成 Broker:Provider × Broker:Environment・既定 paper/sim）。
// 未知の provider / environment・paper と live の矛盾指定は起動時に安全停止（実弾防止・fail-safe は発注抑止側）。
// 選択は合成起点で 1 度だけ解決し、以降は BrokerSelection を参照する（文字列を各所で読み直さない）。
var brokerSelection = BrokerSelection.FromConfiguration(builder.Configuration);

// IADR-0111 閂 0: 実弾（live 階層）は未解禁。OpenD 接続クライアントを構成する前に停止させる
// （＝live を選んでも OpenD への接続も IBrokerAdapter の生成も起きない）。解禁は LiveTradingGate の
// LiveTradingReleased を true にする 1 ファイルの変更に集約され、別 IADR＋IADR-0056 §3 の前提充足を要する。
LiveTradingGate.Ensure(brokerSelection);

// moomoo 選択時は OpenD 接続クライアント（IMoomooTradeClient）を構成し SIMULATE 限定で発注する（実弾を撃たない）。
// #141, IADR-0092: moomoo 時は IMoomooTradeClient を単一インスタンスで DI 共有し、発注アダプタ（IBrokerAdapter）と
// 実照会プローブ（MoomooReservationBrokerProbe）が同一の OpenD 接続を使う（接続を二重化しない）。paper では登録しない。
if (brokerSelection.IsMoomoo)
{
    builder.Services.AddSingleton<IMoomooTradeClient>(sp => new MMApiMoomooTradeClient(
        MoomooBrokerOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<MMApiMoomooTradeClient>()));
}
builder.Services.AddSingleton<IBrokerAdapter>(sp =>
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var moomooClient = sp.GetService<IMoomooTradeClient>(); // moomoo 時のみ登録済み
    return BrokerFactory.Create(brokerSelection, moomooClient, loggerFactory.CreateLogger<MoomooBrokerAdapter>());
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
if (!brokerSelection.IsMoomoo)
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

// #141, IADR-0074: Reserved 滞留の自動リコンサイル（既定無効 Reconciliation:Enabled=false）。
// プローブは差し替え可能で、既定は no-op（常に Indeterminate＝何も解放・終端化しない）。
// #141, IADR-0092: Broker:Provider=moomoo かつ Reconciliation:UseBrokerProbe=true のときだけ実照会プローブ
// （MoomooReservationBrokerProbe・OpenD SIMULATE）を配線する。それ以外（paper／OpenD 無し／既定）は no-op のまま。
// no-op プローブ下では phase-4 自己修復のみ作動し、二重発注を招く解放は構造上起きない。
builder.Services.Configure<ReconciliationOptions>(
    builder.Configuration.GetSection(ReconciliationOptions.SectionName));
if (brokerSelection.IsMoomoo
    && builder.Configuration.GetSection(ReconciliationOptions.SectionName).Get<ReconciliationOptions>()?.UseBrokerProbe == true)
{
    builder.Services.AddSingleton<IReservationBrokerProbe>(sp => new MoomooReservationBrokerProbe(
        sp.GetRequiredService<IMoomooTradeClient>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<MoomooReservationBrokerProbe>()));
}
else
{
    builder.Services.AddSingleton<IReservationBrokerProbe, IndeterminateReservationBrokerProbe>();
}
builder.Services.AddScoped<OrderReservationReconciler>();
builder.Services.AddHostedService<OrderReservationReconciliationService>();

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

// ADR-0001, FR-15, #22 受け入れ基準③: 実効構成（有効な段=宣言由来・選択中ポート実装・構成バージョン）の自己申告。
// メッシュ内部限定エンドポイント GET /internal/introspection（無認可・ネットワーク分離が防御）。
// IADR-0111: 自己申告は正準名 Tier（paper ＜ moomoo-sim ＜ moomoo-live＝本番近接順）で行う。
// 生の Broker:Provider だけでは取引環境（シム／実弾）が判らず「今どの階層か」を実行時に確認できない。
builder.Services.AddAiStockTradingIntrospection(builder.Configuration, ServiceName, b => b.AddPort("broker", brokerSelection.Tier));

var app = builder.Build();

// 起動時にスキーマを最新 Migration へ更新（relational のみ。テストの InMemory はスキップ）。
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrderExecutionDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

app.MapAiStockTradingHealthChecks();
app.MapAiStockTradingIntrospection();

app.Run();

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
