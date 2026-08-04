using AiStockTrading.Report.Application.Adapters;
using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Application.Services;
using AiStockTrading.Report.Domain;
using AiStockTrading.Report.Infrastructure.Composable.Adapters;
using AiStockTrading.Report.Infrastructure.Composable.Polling;
using AiStockTrading.Report.Infrastructure.Foundation.Adapters;
using AiStockTrading.Report.Api.Foundation.Endpoints;
using AiStockTrading.Report.Infrastructure.Foundation.Persistence;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.Shared.KnowledgeBase.Foundation.Extensions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Wolverine;
using Wolverine.Runtime;
using AppSvc = AiStockTrading.Report.Application.Services.ReportService;

const string ServiceName = "ai-stock-trading.report-service";

// #14 Slice A, FR-06/07, IADR-0024: 報告書サービス。確定管理（版番号付き冪等・Keycloak 認可）・確定済み日報方針の照会と
// ヘルスチェックのため WebApplication を用いる。確定の遷移時に ReportConfirmed を発行する（Wolverine）。
//
// IADR-0013: 本 Program.cs の standalone 配線（Wolverine/RabbitMQ・PostgreSQL・Keycloak を shim 経由で組む部分）は
// dev/test/CI でのローカル単体実行のためのもの。本番は platform 統合（#22）で共通基盤に置き換わる。
var builder = WebApplication.CreateBuilder(args);

// IADR-0011: 可観測性（Serilog + OTel）。
builder.Services.AddSerilog((_, logConfig) =>
    logConfig.ConfigureAiStockTradingSerilog(builder.Configuration, ServiceName));
builder.Services.AddAiStockTradingObservability(builder.Configuration, ServiceName);

// ADR-0007: Keycloak 認証（報告書の確定・照会は利用者のみ＝OwnerOnly）。
builder.Services.AddAiStockTradingAuth(builder.Configuration);

// ADR-0001（Database per Service）, IADR-0024: 報告書サービス専有 DB（report_svc）。
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=postgres;Port=5432;Database=report_svc;Username=ai;Password=ai";
builder.Services.AddDbContext<ReportDbContext>(opt => opt.UseNpgsql(connStr));

builder.Services.AddAiStockTradingHealthChecks()
    .AddNpgSql(connStr, tags: ["ready"]);

// 列挙（ReportKind/State）を文字列で送受信する（API の可読性・堅牢性）。
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// 時刻はステートレスのため singleton。DbContext が scoped のため EF ストア・サービスも scoped。
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<IReportStore, EfReportStore>();
builder.Services.AddScoped<AppSvc>();
// FR-06/16, IADR-0032/0071: 報告書生成（数値集計の組み立て＋テンプレート化）。散文は LLM ドラフト。
// 実 LLM は platform LLM ゲートウェイ（POST /complete）へ委譲する（IADR-0071 決定1・#11 IADR-0061 と同形）。
// LlmGateway:BaseUrl 未設定/不正 URI は現行プレースホルダ（定型散文）＝既定オフ。設定時のみ実照会し、送信拒否/失敗/
// タイムアウト/空応答はプレースホルダ散文へ倒す（数値には一切関与しない・FR-16）。選択は解決時に構成を読む
// （起動時読み取りだと WebApplicationFactory の構成上書きに追随しないため）。
//
// IADR-0123 決定1/2, #308: LLM は応答が遅く、しかも所要時間は**報告書種別ごとに違う**（種別ごとに別モデルが
// 割り当たる・IADR-0120）。タイムアウトは種別ごとに解決し（ReportNarrativeTimeouts）、要求単位で適用する。
// HttpClient 自体の Timeout には解決値の最大を置く＝要求単位の打ち切りが壊れても無制限に待たない上限（多層防御）。
builder.Services.AddHttpClient("report-llm",
    c => c.Timeout = NarrativeTimeouts(builder.Configuration).Max);
builder.Services.AddSingleton<PlaceholderReportNarrativeDrafter>();
builder.Services.AddSingleton<IReportNarrativeDrafter>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var baseUrl = cfg["LlmGateway:BaseUrl"];
    // 未設定・不正 URI は安全既定（プレースホルダ＝定型散文）に倒す。
    if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        return sp.GetRequiredService<PlaceholderReportNarrativeDrafter>();

    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("report-llm");
    http.BaseAddress = uri;
    return new HttpReportNarrativeDrafter(http,
        sp.GetRequiredService<ILogger<HttpReportNarrativeDrafter>>(),
        cfg["LlmGateway:Confidentiality"] ?? "internal",
        // IADR-0120 決定1/2: 未設定なら要求ごとに種別から purpose（report-daily/weekly/monthly）を決める。
        // 明示設定時は全種別へ上書き適用する（LlmGateway__Purpose を設定済みのデプロイを壊さない）。
        cfg["LlmGateway:Purpose"],
        // IADR-0061 決定1: 全量ログ（プロンプト・生出力）。既定オフ＝機微を既定でログ基盤へ流さない。
        logPrompts: bool.TryParse(cfg["LlmGateway:LogPrompts"], out var logPrompts) && logPrompts,
        // IADR-0123 決定1: 要求ごとに種別のタイムアウトを解決する（日報 30 秒 / 週報・月報 120 秒が組込既定）。
        timeoutFor: NarrativeTimeouts(cfg).For);
});
// FR-16, #81, IADR-0025/0066: 評価損益の現在値。既定は no-op（実市況未接続＝取得不可）のため評価損益は 0 のまま
// ＝現行挙動。実市況を差し込むとドラフト生成時に建玉ぶんだけ引く。報告書は発注判断を行わない（評価の提示のみ）ため
// リスク管理のような有効化ゲートは持たず、ソース差し替えがそのまま有効化になる。
// 市況断のあいだは保持期限内の前回値へフォールバックする（超過は取得不可＝0。IADR-0066）。
builder.Services.Configure<MarketDataOptions>(builder.Configuration.GetSection(MarketDataOptions.SectionName));
builder.Services.AddSingleton<QuoteCache>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient("marketdata");
// FR-16, #158, IADR-0066/0068: 現在値ソースは構成 MarketData:Provider で選択（既定 no-op＝実接続しない）。報告書は
// ゲートを持たず、実市況ソースへの差し替えがそのまま評価損益の有効化になる（発注判断を伴わないため・IADR-0066 決定 4）。
builder.Services.AddSingleton<IMarketDataSource>(sp => new LastKnownQuoteSource(
    MarketDataSourceFactory.Create(
        sp.GetRequiredService<IOptions<MarketDataOptions>>().Value,
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("marketdata"),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILoggerFactory>()),
    sp.GetRequiredService<QuoteCache>(),
    sp.GetRequiredService<TimeProvider>(),
    TimeSpan.FromSeconds(Math.Max(1, sp.GetRequiredService<IOptions<MarketDataOptions>>().Value.MaxQuoteStalenessSeconds))));
builder.Services.AddScoped<ReportDraftService>();

// FR-08, IADR-0069/0071 決定3: 確定報告書の KB 保存（共有クライアント）。既定は no-op（KnowledgeBase:Documents:BaseUrl
// 未設定＝保存しない＝現行挙動）、設定時のみ実 platform 文書管理へ opt-in。保存は fail-safe（確定を壊さない）。
builder.Services.AddAiStockTradingKnowledgeBase(builder.Configuration);

// FR-06/16, IADR-0115 決定5, #280: 集計対象期間の約定。権威源はリスク管理（#12）の取引台帳であり、
// GET /risk-controls/fills（OwnerOrService・IADR-0051）へ s2s 同期照会する（IADR-0095 と同型）。
// RiskManagement:BaseUrl 未設定/不正 URI は no-op（空列）＝数値 0 の報告書＝現行挙動。照会失敗も空列へ倒す。
builder.Services.AddHttpClient("risk-ledger", c => c.Timeout = TimeSpan.FromSeconds(10))
    .AddAiStockTradingServiceToken(builder.Configuration);
builder.Services.AddSingleton<IPeriodFillSource>(sp =>
{
    var baseUrl = sp.GetRequiredService<IConfiguration>()["RiskManagement:BaseUrl"];
    if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        return new NoOpPeriodFillSource();

    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("risk-ledger");
    http.BaseAddress = uri;
    return new HttpPeriodFillSource(http, sp.GetRequiredService<ILogger<HttpPeriodFillSource>>());
});

// FR-10, UC-06, #330, IADR-0133 決定7: 日報・月報へ載せる「維持率割れによる自動縮小」の記録。
// 権威源への結線は発火元（維持率の供給・#331 / #342）と同時に行う。それまでの既定は空列＝「発動なし」であり、
// **現状は自動縮小があり得ない**ため事実として正しい（照会失敗＝ null とは区別する）。
builder.Services.AddSingleton<IMarginReductionRecordSource, NoMarginReductionRecordSource>();

// FR-06/07, UC-03〜05, ADR-0003, IADR-0115, #280: 日報/週報/月報の自動生成（生成→提示まで・確定はしない）。
// 既定は無効（opt-in）。有効化しない限り常駐は登録されず現行挙動とバイト等価（IADR-0103 と同型）。
builder.Services.Configure<ReportAutoGenerationOptions>(
    builder.Configuration.GetSection(ReportAutoGenerationOptions.SectionName));
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IOptions<ReportAutoGenerationOptions>>().Value.ToSettings());
// FR-09, IADR-0116 決定2, #280: 提示（確定依頼）の通知。NotifyOnDraftPresented（既定 true）が false のときは
// no-op＝イベントを 1 件も発行しない。実送信は通知サービス側の Discord 設定が入って初めて発火する（IADR-0020/0062）。
builder.Services.AddSingleton<IReportDraftPresentedNotifier>(sp =>
    sp.GetRequiredService<IOptions<ReportAutoGenerationOptions>>().Value.NotifyOnDraftPresented
        ? new MessageBusReportDraftPresentedNotifier(sp.GetRequiredService<IWolverineRuntime>(), sp.GetRequiredService<IClock>())
        : new NoOpReportDraftPresentedNotifier());
// EF ストア・ドラフト生成が scoped のため、オーケストレータも scoped（常駐は巡回ごとにスコープを作る）。
builder.Services.AddScoped<ReportAutoGenerator>();
if (builder.Configuration.GetSection(ReportAutoGenerationOptions.SectionName)
        .Get<ReportAutoGenerationOptions>()?.Enabled == true)
    builder.Services.AddHostedService<ReportAutoGenerationService>();

// ADR-0013, IADR-0129, #354: Wolverine（RabbitMQ）。ハンドラは持たず、確定遷移時の ReportConfirmed 発行に用いる。
// キュー名・fan-out・再試行・DLQ の規則は共通ヘルパに閉じている（サービス側でトポロジを選ばない）。
builder.Host.UseWolverine(opts =>
    opts.UseAiStockTradingRabbitMq(ServiceName, builder.Configuration["RabbitMq:ConnectionString"]));

// ADR-0001, FR-15, #22 受け入れ基準③: 実効構成（有効な段=宣言由来・選択中ポート実装・構成バージョン）の自己申告。
// メッシュ内部限定エンドポイント GET /internal/introspection（無認可・ネットワーク分離が防御）。
builder.Services.AddAiStockTradingIntrospection(builder.Configuration, ServiceName, b => b
    .AddPortFromBaseUrl("llm-completion", builder.Configuration["LlmGateway:BaseUrl"], "http", "placeholder")
    .AddPort("market-data", string.IsNullOrWhiteSpace(builder.Configuration["MarketData:Provider"]) ? "noop" : builder.Configuration["MarketData:Provider"]!)
    .AddPortFromBaseUrl("knowledge-base-writer", builder.Configuration["KnowledgeBase:Documents:BaseUrl"], "http", "noop")
    // IADR-0115: 期間約定の供給（権威源へ s2s 照会 or no-op）と、自動生成スケジューラの有効/無効を自己申告する。
    .AddPortFromBaseUrl("period-fills", builder.Configuration["RiskManagement:BaseUrl"], "http", "noop")
    .AddPort("report-auto-generation",
        builder.Configuration.GetSection(ReportAutoGenerationOptions.SectionName)
            .Get<ReportAutoGenerationOptions>()?.Enabled == true ? "scheduler" : "disabled")
    // IADR-0116: 提示（確定依頼）の通知経路。bus=ReportDraftPresented を発行する／noop=発行しない。
    .AddPort("report-draft-notification",
        builder.Configuration.GetSection(ReportAutoGenerationOptions.SectionName)
            .Get<ReportAutoGenerationOptions>()?.NotifyOnDraftPresented == false ? "noop" : "bus"));

var app = builder.Build();

// FR-07, IADR-0071 決定2: 無応答時の既定動作を起動時に解釈してログに残す（設定が実際に消費されていることを可視化する）。
// これによりオペレーターは REPORTS_NORESPONSE_BEHAVIOR の反映を確認できる。期限（翌営業日開場）検知→停止の**実強制**は
// スケジューラ（#22）が本設定（ReportNoResponsePolicy.Decide）を読み取って行う seam であり、本サービス単体では判定のみを持つ。
var noResponseBehavior = ReportNoResponsePolicy.ParseBehavior(builder.Configuration["Reports:NoResponseBehavior"]);
app.Logger.LogInformation(
    "報告書の無応答時の既定動作: {Behavior}（確定を経ない新方針は不適用。期限検知→停止の実強制はスケジューラ #22 が本設定を読む）。",
    noResponseBehavior);

// IADR-0012 準拠: 起動時にスキーマを最新 Migration へ更新（relational のみ。テストの InMemory はスキップ）。
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ReportDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

// 相関ID・認証・認可のミドルウェア。
app.UseAiStockTradingMiddleware();

// /health/live・/health/ready。
app.MapAiStockTradingHealthChecks();
app.MapAiStockTradingIntrospection();

// FR-06/07, UC-03〜05: 報告書のドラフト管理・確定・照会（利用者のみ）。
app.MapReportEndpoints();

app.Run();

// IADR-0071 決定1（#11 IADR-0061 決定2 と同形）: 報告書散文 LLM ゲートウェイのタイムアウト（秒）。
// 未設定・不正・非正値は既定 30 秒（fail-safe）。無限待ちや 0 秒にはしない。
// IADR-0123, #308: 報告書散文 LLM のタイムアウト（種別別）。解決順は
// LlmGateway:TimeoutSecondsByKind:{Daily,Weekly,Monthly} → LlmGateway:TimeoutSeconds（全種別）→ 組込既定。
// 空・非数値・非正値はいずれの段でも「未設定」として次段へ倒す（fail-safe）。
static ReportNarrativeTimeouts NarrativeTimeouts(IConfiguration cfg) => new(
    cfg["LlmGateway:TimeoutSeconds"],
    cfg["LlmGateway:TimeoutSecondsByKind:Daily"],
    cfg["LlmGateway:TimeoutSecondsByKind:Weekly"],
    cfg["LlmGateway:TimeoutSecondsByKind:Monthly"]);

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
