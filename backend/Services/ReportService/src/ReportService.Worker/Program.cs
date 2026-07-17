using AiStockTrading.Report.Application.Adapters;
using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Application.Services;
using AiStockTrading.Report.Worker.Foundation.Adapters;
using AiStockTrading.Report.Worker.Foundation.Endpoints;
using AiStockTrading.Report.Worker.Foundation.Persistence;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using System.Globalization;
using AppSvc = AiStockTrading.Report.Application.Services.ReportService;

const string ServiceName = "ai-stock-trading.report-service";

// #14 Slice A, FR-06/07, IADR-0024: 報告書サービス。確定管理（版番号付き冪等・Keycloak 認可）・確定済み日報方針の照会と
// ヘルスチェックのため WebApplication を用いる。確定の遷移時に ReportConfirmed を発行する（MassTransit）。
//
// IADR-0013: 本 Program.cs の standalone 配線（MassTransit/RabbitMQ・PostgreSQL・Keycloak を shim 経由で組む部分）は
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
// （起動時読み取りだと WebApplicationFactory の構成上書きに追随しないため）。LLM は応答が遅いため HttpClient の
// タイムアウトは構成可能（LlmGateway:TimeoutSeconds・未設定/非正値は既定 30 秒＝fail-safe）。
builder.Services.AddHttpClient("report-llm",
    c => c.Timeout = ParseTimeout(builder.Configuration["LlmGateway:TimeoutSeconds"]));
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
        cfg["LlmGateway:Purpose"] ?? "report-narrative",
        // IADR-0061 決定1: 全量ログ（プロンプト・生出力）。既定オフ＝機微を既定でログ基盤へ流さない。
        logPrompts: bool.TryParse(cfg["LlmGateway:LogPrompts"], out var logPrompts) && logPrompts);
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

// IADR-0011/0024: MassTransit（RabbitMQ）。消費者は持たず、確定遷移時の ReportConfirmed 発行に用いる。
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

// FR-06/07, UC-03〜05: 報告書のドラフト管理・確定・照会（利用者のみ）。
app.MapReportEndpoints();

app.Run();

// IADR-0071 決定1（#11 IADR-0061 決定2 と同形）: 報告書散文 LLM ゲートウェイのタイムアウト（秒）。
// 未設定・不正・非正値は既定 30 秒（fail-safe）。無限待ちや 0 秒にはしない。
static TimeSpan ParseTimeout(string? value) =>
    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
        ? TimeSpan.FromSeconds(seconds)
        : TimeSpan.FromSeconds(30);

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
