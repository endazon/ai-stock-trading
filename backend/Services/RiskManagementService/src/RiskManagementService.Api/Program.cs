using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Infrastructure.Composable;
using AiStockTrading.RiskManagement.Infrastructure.Composable.MarketData;
using AiStockTrading.RiskManagement.Infrastructure.Composable.StageGate;
using AiStockTrading.RiskManagement.Infrastructure.Composable.Steps;
using AiStockTrading.RiskManagement.Api.Foundation.Endpoints;
using AiStockTrading.RiskManagement.Infrastructure.Foundation.Persistence;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using Wolverine;

const string ServiceName = "ai-stock-trading.risk-management-service";

// #12 Slice B, IADR-0011/0029: kill switch/設定変更の HTTP エンドポイント（Keycloak 認可）と
// ヘルスチェックのため WebApplication を用いる。Wolverine のハンドラは常駐のリスナとして稼働する。
//
// IADR-0013: 本 Program.cs の standalone 配線（Wolverine/RabbitMQ・PostgreSQL・Keycloak を
// AiStockTrading.TestSupport.PlatformShim 経由で組む部分）は dev/test/CI でのローカル単体実行のためのもの。
// 本番（実運用）では ai-stock-trading は platform の可変部分へ組み込まれ、バス設定・可観測性・認証などの共通基盤は
// platform 本体の Foundation が提供する（本番統合は #22）。取引ドメインの本番実装は Domain/Application と、
// 本ホストの再利用可能部（TradeDecisionMadeConsumer・EF ストア・エンドポイントハンドラ）である。
var builder = WebApplication.CreateBuilder(args);

// IADR-0011: 可観測性（Serilog + OTel）。
builder.Services.AddSerilog((_, logConfig) =>
    logConfig.ConfigureAiStockTradingSerilog(builder.Configuration, ServiceName));
builder.Services.AddAiStockTradingObservability(builder.Configuration, ServiceName);

// ADR-0004（platform）: Keycloak 認証（利用者のみの操作を OwnerOnly で守る）。
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
// FR-10, FR-12, #257, IADR-0108: SIMULATE 限定のリスク上限プロファイル（既定 false＝本番既定＝現行挙動）。
// 有効時は読み取り時デコレータで金額系の上限とペーパー段階の資金上限だけを差し替える（DB は書き換えない）。
// 実弾段階（Stage 2/3・BrokerProvider.MoomooReal）の資金上限は有効時も本番既定のまま（IADR-0108 決定4）。
builder.Services.Configure<SimulatorProfileOptions>(
    builder.Configuration.GetSection(SimulatorProfileOptions.SectionName));
var simulatorProfileEnabled = builder.Configuration.GetSection(SimulatorProfileOptions.SectionName)
    .Get<SimulatorProfileOptions>()?.Enabled == true;
builder.Services.AddScoped<EfRiskSettingsStore>();
builder.Services.AddScoped<IRiskSettingsStore>(sp => simulatorProfileEnabled
    ? new SimulatorProfileRiskSettingsStore(sp.GetRequiredService<EfRiskSettingsStore>())
    : sp.GetRequiredService<EfRiskSettingsStore>());
builder.Services.AddScoped<IKillSwitchStore, EfKillSwitchStore>();
// FR-10, FR-14, ADR-0009: 取引の一時停止（pause）状態。kill switch と同型・別状態（別テーブル）。
builder.Services.AddScoped<IPauseStore, EfPauseStore>();
builder.Services.AddScoped<ILockoutStore, EfLockoutStore>();
builder.Services.AddScoped<ISettingsChangeLog, EfSettingsChangeLog>();
// FR-20, UC-06, IADR-0041/0070: 段階ゲートの遷移台帳（追記専用）と段階別実績（単一行・fail-safe 既定）。
builder.Services.AddScoped<IStageGateStore, EfStageGateStore>();
builder.Services.AddScoped<IStagePerformanceStore, EfStagePerformanceStore>();
// FR-20, FR-11, #387, IADR-0148: 段階ゲートの「統制違反 0 件」（クラス C 限定）の供給元＝発注審査の観測ログ。
// **未記録は未供給（null）として返り、条件1 を未充足にする**（0 件と同一視しない＝#387 の fail-open を塞ぐ）。
// DbContext が scoped のため本ストアも scoped。
builder.Services.AddScoped<IControlViolationObservationStore, EfControlViolationObservationStore>();
// FR-20, FR-12, #386, IADR-0149: 段階ゲートの「最小取引件数 100 件」（§4.1 条件3）の供給元＝約定の観測ログ。
// 計上単位は「約定した新規建て注文 1 件」（DecisionId 主キー）。未記録は 0 件＝昇格しない fail-safe。
builder.Services.AddScoped<IStage1FillObservationStore, EfStage1FillObservationStore>();
// FR-20, FR-12, #385, IADR-0150: 段階ゲートの「60 営業日」（§4.2 の期間カウント）の供給元＝稼働の観測ログ。
// 1 取引日 1 発注先 1 行。未記録は 0 日＝期間条件が未充足＝昇格しない fail-safe。
builder.Services.AddScoped<IStage1TradingDayObservationStore, EfStage1TradingDayObservationStore>();
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
    // #257, IADR-0108: 基準資金（台帳射影の初期資金）もプロファイルに追随させる。無効（既定）は null＝本番既定。
    var initialCapital = simulatorProfileEnabled
        ? AiStockTrading.RiskManagement.Domain.SimulatorTradingDefaults.InitialCapital
        : (decimal?)null;
    return options.Value.EnableMarkToMarket
        ? new LedgerPortfolioStateProvider(
            ledger, clock, sp.GetRequiredService<ICurrentPriceSource>(), initialCapital)
        : new LedgerPortfolioStateProvider(ledger, clock, currentPrices: null, initialCapital);
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
// #333, IADR-0136: 段階の発注可能額は**総資金比**で保持されるため、SIMULATE プロファイル用の差し替え
// （旧 SimulatorTradingDefaults.CreateStagePolicy）は不要になった。比率はスケール不変であり、基準資金を
// プロファイル値へ注入すれば上限額は比例して自動的に上がる（IADR-0130 決定6 と同じ論法）。
// 結果として「検証用フラグで実弾段階の上限を動かさない」という不変条件（IADR-0108 決定4）は
// 差し替え対象そのものが無いことで構造的に成立する。
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
// FR-10, FR-11, UC-06, #292, IADR-0117: 利用者（owner）による建玉の手仕舞い（POST /risk-controls/positions/close）。
// 統制ストアを依存に持たない＝手仕舞いは kill switch・日次損失ロックアウト・一時停止で止まらない（FR-10 本文）。
builder.Services.AddScoped<PositionCloseService>();
// FR-10, UC-06, ADR-0016 決定7, #330, IADR-0133: 維持率割れによる建玉の自動縮小（システム自動・AI 非介在）。
// 統制ストアもスクリーニングも依存に持たない＝3 統制が成立していても動く（UC-06・ADR-0009）。
// 維持率の供給元は未実装のため既定は「供給なし」＝発動しない（#342 / #331 が実装を入れる）。
builder.Services.AddSingleton<IMaintenanceMarginSnapshotSource, UnavailableMaintenanceMarginSnapshotSource>();
builder.Services.AddScoped<MaintenanceMarginReductionService>();
// FR-05, FR-10, #292, #305, IADR-0118, IADR-0124: 建玉突合の報告可否（連続観測条件・シグネチャ dedup）。
// 追跡状態は DB 単一行＋並行トークンで持ちレプリカ間で一貫させる（インメモリでは replicas>1 で観測が Pod へ
// 分散し、乖離が例外もログも出さずに恒久未報告になり得た）。DbContext が scoped のため両者とも scoped。
builder.Services.AddScoped<IPositionDriftStateStore, EfPositionDriftStateStore>();
builder.Services.AddScoped<PositionDriftTracker>();

// ADR-0013, IADR-0129, #354: Wolverine（RabbitMQ）。TradeDecisionMade を購読し承認/拒否を発行、
// StopLossTriggered を購読し LLM 迂回で決済（Close）を発行する。承認・約定・訂正・取消は取引台帳（IADR-0018）と
// 注文アクティビティ（FR-19 / #154 / IADR-0067）へ射影し、BacktestEvaluated は段階別実績へ（#164 / IADR-0089）、
// BrokerPositionsObserved は台帳との乖離検知へ（#292 / IADR-0118）回す。
//
// **本サービスは OrderApproved を発行しつつ自分でも購読している。**Wolverine の既定では発行が自プロセス内へ
// 閉じ、OrderExecutionService へ一通も届かない（発注が一件も執行されない）。共通ヘルパが必ず
// DisableConventionalLocalRouting を適用してこれを止める（IADR-0129 決定 3）。
//
// **OrderApproved / OrderExecuted は 1 イベントにつき 2 ハンドラ（台帳・活動射影）が動く。**MassTransit では
// キューが分かれていたが Wolverine では 1 本のチェーンに統合され、再試行で両方が再実行される。
// 双方の書き込みが冪等であることが安全性の根拠であり、コード上の根拠は IADR-0129 決定 10 に記載した。
builder.Host.UseWolverine(opts => opts.UseAiStockTradingRabbitMq(
    ServiceName,
    builder.Configuration["RabbitMq:ConnectionString"],
    typeof(TradeDecisionMadeHandler).Assembly));

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

// FR-10, FR-19, UC-06, ADR-0003, ADR-0007: kill switch 操作・設定変更（利用者のみ）。
app.MapRiskControlEndpoints();

app.Run();

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
