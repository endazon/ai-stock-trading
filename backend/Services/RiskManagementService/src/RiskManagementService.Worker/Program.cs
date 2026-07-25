using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Worker.Composable.MarketData;
using AiStockTrading.RiskManagement.Worker.Composable.StageGate;
using AiStockTrading.RiskManagement.Worker.Composable.Steps;
using AiStockTrading.RiskManagement.Worker.Foundation.Endpoints;
using AiStockTrading.RiskManagement.Worker.Foundation.Persistence;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;
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
// FR-10, FR-14, ADR-0009: 取引の一時停止（pause）状態。kill switch と同型・別状態（別テーブル）。
builder.Services.AddScoped<IPauseStore, EfPauseStore>();
builder.Services.AddScoped<ILockoutStore, EfLockoutStore>();
builder.Services.AddScoped<ISettingsChangeLog, EfSettingsChangeLog>();
// FR-20, UC-06, IADR-0041/0070: 段階ゲートの遷移台帳（追記専用）と段階別実績（単一行・fail-safe 既定）。
builder.Services.AddScoped<IStageGateStore, EfStageGateStore>();
builder.Services.AddScoped<IStagePerformanceStore, EfStagePerformanceStore>();
// FR-20, FR-09, IADR-0085, #189: 撤退の非停止（ペーパー乖離）降格提案の通知重複排除（durable な通知済みシグネチャ・単一行）。
builder.Services.AddScoped<IWithdrawalNotificationStore, EfWithdrawalNotificationStore>();
// FR-10, FR-05, IADR-0018: 保有・損益は取引台帳（OrderApproved/OrderExecuted）からの純射影で供給する。
// DbContext が scoped のため台帳ストア・プロバイダも scoped。
builder.Services.AddScoped<IPortfolioLedgerStore, EfPortfolioLedgerStore>();
// FR-10, #81, IADR-0066: 含み損益・DD の時価評価。既定（EnableMarkToMarket=false）は現在値を注入せず従来どおり
// 含み 0・DD 0（IADR-0008/0018）。有効化すると DrawdownRatio が非 0 になり最大DD の取引ゲートの入力が変わるため、
// 実市況の live 検証を経てから人手で切り替える。現在値ソース自体も既定 no-op（実接続しない）。
builder.Services.Configure<MarketDataOptions>(builder.Configuration.GetSection(MarketDataOptions.SectionName));
// FR-10, #158, IADR-0068: 現在値ソースは構成 MarketData:Provider で選択（既定・空・未知は no-op＝実接続しない）。
// finnhub 指定＋API キーありのときだけ実市況になる。補充（QuoteRefreshService）は EnableMarkToMarket=false の
// 既定では起動しないため、Provider を指定しても実際の取得はゲートを人手で ON にするまで起きない（IADR-0066 決定 4）。
builder.Services.AddHttpClient("marketdata");
builder.Services.AddSingleton<IMarketDataSource>(sp => MarketDataSourceFactory.Create(
    sp.GetRequiredService<IOptions<MarketDataOptions>>().Value,
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("marketdata"),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILoggerFactory>()));
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
// FR-10, FR-14, UC-06/07, ADR-0009: 一時停止/再開の操作と、稼働状態の集約照会（/status・表示専用）。
builder.Services.AddScoped<PauseService>();
builder.Services.AddScoped<RiskStatusService>();
builder.Services.AddScoped<RiskSettingsService>();
// FR-20, UC-06, ADR-0008, IADR-0041/0070: 段階ゲート遷移サービス。段階ゲート方針は TradingDefaults を参照（変更しない）。
// 撤退の自動安全側は KillSwitchService を通す（自動＝停止・承認＝段階変更）。
builder.Services.AddSingleton(AiStockTrading.RiskManagement.Domain.TradingDefaults.CreateStagePolicy());
builder.Services.AddScoped<StageGateService>();
// FR-20, FR-11, FR-09, ADR-0008, IADR-0083, #166: 撤退の定期評価ドライバ。EvaluateWithdrawal を定時駆動し、新規に
// 自動停止したときだけ WithdrawalTriggered を発行する。既定は無効（opt-in・安全側）。有効化しても実 DD 未供給の
// 既定実績では発火しない（QuoteRefreshService と同じく副作用を伴う背景処理は既定起動しない）。
builder.Services.Configure<WithdrawalEvaluationOptions>(
    builder.Configuration.GetSection(WithdrawalEvaluationOptions.SectionName));
if (builder.Configuration.GetSection(WithdrawalEvaluationOptions.SectionName)
        .Get<WithdrawalEvaluationOptions>()?.Enabled == true)
    builder.Services.AddHostedService<WithdrawalEvaluationService>();
// FR-20, FR-10, ADR-0008, IADR-0103, #164: 実DD（観測最大ドローダウン）の供給ドライバ。時価評価つき DrawdownRatio を
// 定時サンプリングして段階別実績へ単調 latch し、撤退基準（実DD ≥ バックテスト最大DD × 1.5）の入力を満たす。
// 供給は Risk 専有データ（取引台帳＋時価評価）のみで完結する（他サービスへの s2s 照会・イベント購読は不要）。
// 既定は無効（opt-in・安全側）。有効化しても EnableMarkToMarket が既定無効なら DD は常に 0 で書き込みも起きない。
builder.Services.Configure<ObservedDrawdownRefreshOptions>(
    builder.Configuration.GetSection(ObservedDrawdownRefreshOptions.SectionName));
if (builder.Configuration.GetSection(ObservedDrawdownRefreshOptions.SectionName)
        .Get<ObservedDrawdownRefreshOptions>()?.Enabled == true)
    builder.Services.AddHostedService<ObservedDrawdownRefreshService>();
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
    // FR-20, FR-15, #164, IADR-0089: バックテスト verdict（BacktestEvaluated）を購読し段階別実績へ射影する（Stage 0→1 解錠）。
    x.AddConsumer<BacktestEvaluatedProjectionConsumer>();
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
builder.Services.AddAiStockTradingIntrospection(builder.Configuration, ServiceName, b => b
    .AddPort("market-data", string.IsNullOrWhiteSpace(builder.Configuration["MarketData:Provider"]) ? "noop" : builder.Configuration["MarketData:Provider"]!));

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
app.MapAiStockTradingIntrospection();

// FR-10, UC-06, ADR-0007: kill switch 操作・設定変更（利用者のみ）。
app.MapRiskControlEndpoints();

app.Run();

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
