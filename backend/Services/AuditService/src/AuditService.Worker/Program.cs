using AiStockTrading.Audit.Application.Adapters;
using AiStockTrading.Audit.Application.Ports;
using AiStockTrading.Audit.Worker.Composable.Steps;
using AiStockTrading.Audit.Worker.Foundation.Endpoints;
using AiStockTrading.Audit.Worker.Foundation.Persistence;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Serilog;

const string ServiceName = "ai-stock-trading.audit-service";

// #17 Slice A, FR-11, IADR-0019: 監査ログサービス。全ドメインイベントを購読して監査台帳へ記録し、OwnerOnly の照会
// エンドポイントとヘルスチェックのため WebApplication を用いる。MassTransit コンシューマは IHostedService として稼働する。
//
// IADR-0013: 本 Program.cs の standalone 配線（MassTransit/RabbitMQ・PostgreSQL・Keycloak を shim 経由で組む部分）は
// dev/test/CI でのローカル単体実行のためのもの。本番は platform 統合（#22）で共通基盤に置き換わる。
var builder = WebApplication.CreateBuilder(args);

// IADR-0011: 可観測性（Serilog + OTel）。
builder.Services.AddSerilog((_, logConfig) =>
    logConfig.ConfigureAiStockTradingSerilog(builder.Configuration, ServiceName));
builder.Services.AddAiStockTradingObservability(builder.Configuration, ServiceName);

// ADR-0004（platform）, ADR-0007: Keycloak 認証（監査照会は利用者のみ＝OwnerOnly）。
builder.Services.AddAiStockTradingAuth(builder.Configuration);

// ADR-0001（Database per Service）, IADR-0019: 監査サービス専有 DB（audit_svc）。
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=postgres;Port=5432;Database=audit_svc;Username=ai;Password=ai";
builder.Services.AddDbContext<AuditDbContext>(opt => opt.UseNpgsql(connStr));

// DB 到達性の readiness ヘルスチェック。
builder.Services.AddAiStockTradingHealthChecks()
    .AddNpgSql(connStr, tags: ["ready"]);

// FR-11: 記録時刻はステートレスのため singleton。監査台帳ストアは DbContext が scoped のため scoped。
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<IAuditEventStore, EfAuditEventStore>();

// IADR-0011/0019: MassTransit（RabbitMQ）。全ドメインイベントを購読して監査台帳へ記録する。
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<PriceMovementDetectedAuditConsumer>();
    x.AddConsumer<StopLossTriggeredAuditConsumer>();
    x.AddConsumer<TradeDecisionMadeAuditConsumer>();
    x.AddConsumer<OrderApprovedAuditConsumer>();
    x.AddConsumer<OrderRejectedAuditConsumer>();
    x.AddConsumer<OrderExecutedAuditConsumer>();
    // FR-19/#154, IADR-0067: 注文の訂正・取消（注文履歴テレメトリ）も監査台帳へ記録する。
    x.AddConsumer<OrderModifiedAuditConsumer>();
    x.AddConsumer<OrderCancelledAuditConsumer>();
    // FR-17/FR-07: 設定変更（#19）・報告書確定（#14）も監査台帳へ記録する。
    x.AddConsumer<AssumptionsChangedAuditConsumer>();
    x.AddConsumer<ReportConfirmedAuditConsumer>();
    // NFR（費用）/FR-01: 費用しきい値到達（#23）・情報収集完了（#9）も監査台帳へ記録する（全イベントの時系列記録・FR-11）。
    x.AddConsumer<CostThresholdReachedAuditConsumer>();
    x.AddConsumer<LlmCostIncurredAuditConsumer>();
    x.AddConsumer<InformationCollectedAuditConsumer>();
    // FR-20/#167, IADR-0082: 段階ゲートの遷移（#20 後続）も中央監査台帳へ集約する（全イベントの時系列記録・FR-11）。
    x.AddConsumer<StageTransitionedAuditConsumer>();
    // FR-20/#166, IADR-0083: 撤退基準到達（自動安全側の発火）も中央監査台帳へ集約する（全イベントの時系列記録・FR-11）。
    x.AddConsumer<WithdrawalTriggeredAuditConsumer>();
    // FR-20/FR-15/#164, IADR-0089: バックテスト verdict（Stage 0 合格判定 #16・Stage 0→1 解錠）も中央監査台帳へ集約する（FR-11）。
    x.AddConsumer<BacktestEvaluatedAuditConsumer>();
    // UC-01/FR-09/FR-07/#210: 日報未確定による取引スキップ（取引判断 #11）も中央監査台帳へ集約する（全イベントの時系列記録・FR-11）。
    x.AddConsumer<DailyPolicyUnconfirmedAuditConsumer>();
    // FR-06/07/09/#280, IADR-0116: 報告書ドラフトの提示（自動生成スケジューラ）も中央監査台帳へ集約する（FR-11）。
    x.AddConsumer<ReportDraftPresentedAuditConsumer>();
    // FR-10/FR-11/#292, IADR-0117: 利用者による建玉の手仕舞い要求（誰が・なぜ）も中央監査台帳へ集約する（FR-11）。
    x.AddConsumer<PositionCloseRequestedAuditConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672");
        // 一時的失敗は再試行し、継続失敗はデッドレターへ退避する（回復性）。
        cfg.UseAiStockTradingRetry();
        cfg.ConfigureEndpoints(ctx);
    });
});

// ADR-0001, FR-15, #22 受け入れ基準③: 実効構成（有効な段=宣言由来・選択中ポート実装・構成バージョン）の自己申告。
// メッシュ内部限定エンドポイント GET /internal/introspection（無認可・ネットワーク分離が防御）。
builder.Services.AddAiStockTradingIntrospection(builder.Configuration, ServiceName);

var app = builder.Build();

// IADR-0012 準拠: 起動時にスキーマを最新 Migration へ更新（relational のみ。テストの InMemory はスキップ）。
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

// 相関ID・認証・認可のミドルウェア。
app.UseAiStockTradingMiddleware();

// /health/live・/health/ready。
app.MapAiStockTradingHealthChecks();
app.MapAiStockTradingIntrospection();

// FR-11, UC-07: 監査台帳の照会（利用者のみ）。
app.MapAuditQueryEndpoints();

app.Run();

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
