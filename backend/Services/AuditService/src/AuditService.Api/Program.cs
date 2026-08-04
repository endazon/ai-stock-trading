using AiStockTrading.Audit.Application.Adapters;
using AiStockTrading.Audit.Application.Ports;
using AiStockTrading.Audit.Infrastructure.Composable.Steps;
using AiStockTrading.Audit.Api.Foundation.Endpoints;
using AiStockTrading.Audit.Infrastructure.Foundation.Persistence;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Wolverine;

const string ServiceName = "ai-stock-trading.audit-service";

// #17 Slice A, FR-11, IADR-0019: 監査ログサービス。全ドメインイベントを購読して監査台帳へ記録し、OwnerOnly の照会
// エンドポイントとヘルスチェックのため WebApplication を用いる。Wolverine のハンドラは常駐のリスナとして稼働する。
//
// IADR-0013: 本 Program.cs の standalone 配線（Wolverine/RabbitMQ・PostgreSQL・Keycloak を shim 経由で組む部分）は
// dev/test/CI でのローカル単体実行のためのもの。本番は platform 統合（#22）で共通基盤に置き換わる。
var builder = WebApplication.CreateBuilder(args);

// IADR-0011: 可観測性（Serilog + OTel）。
builder.Services.AddSerilog((_, logConfig) =>
    logConfig.ConfigureAiStockTradingSerilog(builder.Configuration, ServiceName));
builder.Services.AddAiStockTradingObservability(builder.Configuration, ServiceName);

// ADR-0004（platform）: Keycloak 認証（監査照会は利用者のみ＝OwnerOnly）。
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

// ADR-0013, IADR-0129, #354: Wolverine（RabbitMQ）。全ドメインイベントを購読して監査台帳へ記録する。
// ハンドラは明示登録ではなくアセンブリ走査で発見されるため、ハンドラを持つアセンブリ（Infrastructure）を明示する。
// 購読対象は AuditEventHandlers.cs の 21 種（市場・注文チェーン・訂正取消 FR-19/#154／設定変更 FR-17・報告書 FR-07／
// 費用 NFR・情報収集 FR-01／段階ゲート FR-20/#167・撤退 #166・バックテスト verdict #164／日報未確定 #210／
// 報告書ドラフト提示 #280／建玉の手仕舞い要求 #292・ブローカ建玉観測と乖離 #292）。
// **契約イベントの追加に対する追随漏れ**（FR-11「全イベントの時系列記録」の穴）は
// AuditConsumerCoverageTests がリフレクションで検出する。
// 再試行（2s/10s/30s）と <queue>_error への退避は共通ヘルパに閉じている（IADR-0129 決定 5）。
builder.Host.UseWolverine(opts => opts.UseAiStockTradingRabbitMq(
    ServiceName,
    builder.Configuration["RabbitMq:ConnectionString"],
    typeof(PriceMovementDetectedAuditHandler).Assembly));

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
