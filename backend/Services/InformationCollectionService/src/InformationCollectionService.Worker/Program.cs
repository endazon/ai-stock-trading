using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.InformationCollection.Domain;
using AiStockTrading.InformationCollection.Worker.Composable.Adapters;
using AiStockTrading.InformationCollection.Worker.Composable.Polling;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using MassTransit;
using Serilog;
using AppSvc = AiStockTrading.InformationCollection.Application.Services.InformationCollectionService;

const string ServiceName = "ai-stock-trading.information-collection-service";

// #9 Slice A, FR-01, IADR-0022: 情報収集サービス。定時ポーリングで収集→正規化→サニタイズ→KB 保存→収集完了イベント発行。
// ヘルスチェックの HTTP サーフェスのため WebApplication を用いる（DB・認可なし）。外部情報源・KB 保存は既定で無効（安全既定）。
//
// IADR-0013: 本 Program.cs の standalone 配線（MassTransit/RabbitMQ を shim 経由で組む部分）は dev/test/CI での
// ローカル単体実行のためのもの。本番は platform 統合（#22）で共通基盤に置き換わる。
var builder = WebApplication.CreateBuilder(args);

// IADR-0011: 可観測性（Serilog + OTel）。
builder.Services.AddSerilog((_, logConfig) =>
    logConfig.ConfigureAiStockTradingSerilog(builder.Configuration, ServiceName));
builder.Services.AddAiStockTradingObservability(builder.Configuration, ServiceName);

// liveness ヘルスチェック（DB を持たない）。
builder.Services.AddAiStockTradingHealthChecks();

// 収集ポーリングの構成（間隔）。
builder.Services.Configure<CollectionOptions>(builder.Configuration.GetSection(CollectionOptions.SectionName));

// FR-01, ADR-0004: 案A+ の許可リスト（許可された情報源のみ受理）。
builder.Services.AddSingleton(SourceAllowlist.Default);

// FR-01, IADR-0022: 情報源の選択（安全既定 no-op）。実 Finnhub 接続は Collection:Source:Provider=finnhub＋APIキーで明示有効化。
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IInformationSource>(sp => InformationSourceFactory.Create(
    builder.Configuration["Collection:Source:Provider"],
    builder.Configuration["Collection:Source:Finnhub:ApiKey"],
    builder.Configuration.GetSection("Collection:Source:Finnhub:Symbols").Get<string[]>() ?? [],
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("collection"),
    sp.GetRequiredService<ILoggerFactory>()));

// FR-01, FR-08: KB シンク（既定は no-op/ログ。実 platform KB 連携は #18）。
builder.Services.AddSingleton<IKnowledgeBaseSink, LoggingKnowledgeBaseSink>();

// 収集オーケストレーション（scoped）。ポーリングは巡回ごとに DI スコープを作る。
builder.Services.AddScoped<AppSvc>();

// NFR（費用）, IADR-0031: 定時サイクルの費用統制（#23）ゲート。CostControl:BaseUrl 未設定/不正 URI は Placeholder
// （Normal＝停止も間隔延長もしない）＝安全既定でゲート。設定時のみ GET /costs/state を同期照会する（解決時に構成を読む）。
// 同期クリティカルパス（巡回冒頭）に置くため短いタイムアウトを設定する。
builder.Services.AddHttpClient("cost", c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddSingleton<PlaceholderCostControlGate>();
builder.Services.AddScoped<ICostControlGate>(sp =>
{
    var baseUrl = sp.GetRequiredService<IConfiguration>()["CostControl:BaseUrl"];
    if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        return sp.GetRequiredService<PlaceholderCostControlGate>();

    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("cost");
    http.BaseAddress = uri;
    return new HttpCostControlGate(http, sp.GetRequiredService<ILogger<HttpCostControlGate>>());
});

// IADR-0011/0022: MassTransit（RabbitMQ）。消費者は持たず、収集完了時の InformationCollected 発行に用いる。
builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((_, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672");
        cfg.UseAiStockTradingRetry();
    });
});

// 定時ポーリング（収集→保存→イベント発行）。
builder.Services.AddHostedService<CollectionPollingService>();

var app = builder.Build();

// /health/live・/health/ready。
app.MapAiStockTradingHealthChecks();

app.Run();

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
