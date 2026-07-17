using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Worker.Composable.MarketData;
using AiStockTrading.RiskManagement.Worker.Composable.Steps;
using AiStockTrading.RiskManagement.Worker.Foundation.Endpoints;
using AiStockTrading.RiskManagement.Worker.Foundation.Persistence;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;

const string ServiceName = "ai-stock-trading.risk-management-service";

// #12 Slice B, IADR-0011/0029: kill switch/設定変更の HTTP エンドポイント（Keycloak 認可）と
// ヘルスチェックのため WebApplication を用いる。MassTransit コンシューマは IHostedService として稼働する。
//
// IADR-0013: 本 Program.cs の standalone 配線（MassTransit/RabbitMQ・PostgreSQL・Keycloak を
// AiStockTrading.TestSupport.PlatformShim 経由で組む部分）は dev/test/CI でのローカル単体実行のためのもの。
// 本番（実運用）では ai-stock-trading は platform の可変部分へ組み込まれ、バス設定・可観測性・認証などの共通基盤は
// platform 本体の Foundation が提供する（本番統合は #22）。取引ドメインの本番実装は Domain/Application と、
// 本ホストの再利用可能部（TradeDecisionMadeConsumer・EF ストア・エンドポイントハンドラ）である。
var builder = WebApplication.CreateBuilder(args);

// IADR-0011: 可観測性（Serilog + OTel）。
builder.Services.AddSerilog((_, logConfig) =>
    logConfig.ConfigureAiStockTradingSerilog(builder.Configuration, ServiceName));
builder.Services.AddAiStockTradingObservability(builder.Configuration, ServiceName);

// ADR-0004（platform）, ADR-0007: Keycloak 認証（利用者のみの操作を OwnerOnly で守る）。
builder.Services.AddAiStockTradingAuth(builder.Configuration);

// ADR-0001（Database per Service）, IADR-0012: リスク管理専有 DB（risk_management_svc）。
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=postgres;Port=5432;Database=risk_management_svc;Username=ai;Password=ai";
builder.Services.AddDbContext<RiskManagementDbContext>(opt => opt.UseNpgsql(connStr));

// DB 到達性の readiness ヘルスチェック。
builder.Services.AddAiStockTradingHealthChecks()
    .AddNpgSql(connStr, tags: ["ready"]);

// --- リスク管理のポートとサービス（Slice A）を配線する ---
// 時刻・営業日はステートレスのため singleton。
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IBusinessCalendar, WeekendBusinessCalendar>();
// DbContext が scoped のため EF ストアも scoped。
builder.Services.AddScoped<IRiskSettingsStore, EfRiskSettingsStore>();
builder.Services.AddScoped<IKillSwitchStore, EfKillSwitchStore>();
builder.Services.AddScoped<ILockoutStore, EfLockoutStore>();
builder.Services.AddScoped<ISettingsChangeLog, EfSettingsChangeLog>();
// FR-10, FR-05, IADR-0018: 保有・損益は取引台帳（OrderApproved/OrderExecuted）からの純射影で供給する。
// DbContext が scoped のため台帳ストア・プロバイダも scoped。
builder.Services.AddScoped<IPortfolioLedgerStore, EfPortfolioLedgerStore>();
// FR-10, #81, IADR-0066: 含み損益・DD の時価評価。既定（EnableMarkToMarket=false）は現在値を注入せず従来どおり
// 含み 0・DD 0（IADR-0008/0018）。有効化すると DrawdownRatio が非 0 になり最大DD の取引ゲートの入力が変わるため、
// 実市況の live 検証を経てから人手で切り替える。現在値ソース自体も既定 no-op（実接続しない）。
builder.Services.Configure<MarketDataOptions>(builder.Configuration.GetSection(MarketDataOptions.SectionName));
builder.Services.AddSingleton<IMarketDataSource, NoOpMarketDataSource>();
builder.Services.AddSingleton<QuoteCache>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IPortfolioStateProvider>(sp =>
{
    var ledger = sp.GetRequiredService<IPortfolioLedgerStore>();
    var clock = sp.GetRequiredService<IClock>();
    var options = sp.GetRequiredService<IOptions<MarketDataOptions>>();
    // 無効（既定）なら現在値ソースを注入しない＝含み 0・DD 0 の現行挙動をそのまま保つ。
    return options.Value.EnableMarkToMarket
        ? new LedgerPortfolioStateProvider(ledger, clock, sp.GetRequiredService<ICurrentPriceSource>())
        : new LedgerPortfolioStateProvider(ledger, clock);
});
builder.Services.AddScoped<ICurrentPriceSource, CachedCurrentPriceSource>();
// 現在値の補充は背景で行う（発注判断の同期経路にネットワーク往復を持ち込まない）。
// 無効（既定）なら補充自体を起動しない＝台帳への巡回アクセスも発生させない。
if (builder.Configuration.GetSection(MarketDataOptions.SectionName).Get<MarketDataOptions>()?.EnableMarkToMarket == true)
    builder.Services.AddHostedService<QuoteRefreshService>();
builder.Services.AddScoped<PortfolioSnapshotBuilder>();
// FR-04/10, IADR-0029: 取引判断へ供給するサイジング文脈（設定＋ポートフォリオ状態から導出）。
builder.Services.AddScoped<SizingContextService>();
// FR-03/10, IADR-0030: 市場監視へ供給する保有ポジション（#63 台帳の射影＋損切り価格の近似導出）。
builder.Services.AddScoped<OpenPositionsService>();
builder.Services.AddScoped<KillSwitchService>();
builder.Services.AddScoped<RiskSettingsService>();
// FR-19, #154, IADR-0006/0040/0067: 相場操縦検出器（#49）を本番有効化する。検知アルゴリズム
// （ManipulativeOrderPatternDetector＋ManipulationPatternAnalyzer）に、注文履歴テレメトリ（注文系イベントの
// Risk 専有 DB への射影・#154）から IOrderActivitySource を供給する。IOrderActivitySource は同期契約かつ
// 発注審査のホットパス上のため、供給は他サービスへの同期照会ではなく射影とする（IADR-0018 と同型・IADR-0067）。
// DbContext が scoped のため射影ストア・供給源も scoped。検知設定は静的既定（TradingDefaults）で改ざん不可（IADR-0040）。
builder.Services.AddScoped<IOrderActivityStore, EfOrderActivityStore>();
builder.Services.AddScoped<IOrderActivitySource, EfOrderActivitySource>();
builder.Services.AddSingleton(
    AiStockTrading.RiskManagement.Domain.TradingDefaults.CreateManipulationDetectionSettings());
builder.Services.AddScoped<AiStockTrading.RiskManagement.Domain.IManipulativeOrderPatternDetector,
    ManipulativeOrderPatternDetector>();
// OrderScreeningService は検出器を GetService（null 許容）で受けるため、上の登録により相場操縦判定が有効になる。
builder.Services.AddScoped(sp => new OrderScreeningService(
    sp.GetRequiredService<IRiskSettingsStore>(),
    sp.GetRequiredService<PortfolioSnapshotBuilder>(),
    sp.GetRequiredService<ILockoutStore>(),
    sp.GetRequiredService<IClock>(),
    sp.GetRequiredService<IBusinessCalendar>(),
    sp.GetService<AiStockTrading.RiskManagement.Domain.IManipulativeOrderPatternDetector>()));
// FR-10, ADR-0003, IADR-0015: 損切りの機械執行（StopLossTriggered → Close の OrderApproved・無条件）。
builder.Services.AddScoped<StopLossExecutionService>();

// ADR-0003, IADR-0011: MassTransit（RabbitMQ）。TradeDecisionMade を購読し承認/拒否を発行、
// StopLossTriggered を購読し LLM 迂回で決済（Close）を発行する。
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<TradeDecisionMadeConsumer>();
    x.AddConsumer<StopLossTriggeredConsumer>();
    // IADR-0018: 承認・約定を購読して取引台帳へ射影する（IPortfolioStateProvider の実データ源）。
    x.AddConsumer<OrderApprovedLedgerConsumer>();
    x.AddConsumer<OrderExecutedLedgerConsumer>();
    // FR-19, #154, IADR-0067: 承認・約定・訂正・取消を購読して注文アクティビティへ射影する（相場操縦検知の入力源）。
    x.AddConsumer<OrderApprovedActivityConsumer>();
    x.AddConsumer<OrderExecutedActivityConsumer>();
    x.AddConsumer<OrderModifiedActivityConsumer>();
    x.AddConsumer<OrderCancelledActivityConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672");
        // 一時的失敗は再試行し、継続失敗はデッドレターへ退避する（回復性）。
        cfg.UseAiStockTradingRetry();
        cfg.ConfigureEndpoints(ctx);
    });
});

var app = builder.Build();

// IADR-0012: 起動時にスキーマを最新 Migration へ更新（relational のみ。テストの InMemory はスキップ）。
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RiskManagementDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

// 相関ID・認証・認可のミドルウェア。
app.UseAiStockTradingMiddleware();

// /health/live・/health/ready。
app.MapAiStockTradingHealthChecks();

// FR-10, UC-06, ADR-0007: kill switch 操作・設定変更（利用者のみ）。
app.MapRiskControlEndpoints();

app.Run();

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
