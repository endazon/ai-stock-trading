using AiStockTrading.TradeDecision.Application.Adapters;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Application.Services;
using AiStockTrading.TradeDecision.Worker.Composable.Adapters;
using AiStockTrading.TradeDecision.Worker.Composable.Steps;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using MassTransit;
using Serilog;
using System.Globalization;

const string ServiceName = "ai-stock-trading.trade-decision-service";

// #11 Slice A / #21 (FR-02), IADR-0013/0017/0023: ヘルスチェックの HTTP サーフェスのため WebApplication を用いる。
// 価格変動（PriceMovementDetected・イベント駆動）と収集完了（InformationCollected・定時）の両系統を MassTransit
// コンシューマとして購読し、市場カレンダー（IMarketCalendar）で休場日をゲートしつつ取引判断で合流する。判断はステートレス（DB なし）。
//
// IADR-0013: 本 Program.cs の standalone 配線（MassTransit/RabbitMQ を shim 経由で組む部分）は dev/test/CI の
// ローカル単体実行のためのもの。本番は platform 統合（#22）で共通基盤に置き換わる。
// IADR-0017: 実 LLM/実データはプレースホルダ（安全既定＝取引しない）。実 LLM（platform /complete）・実データは後続。
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((_, logConfig) =>
    logConfig.ConfigureAiStockTradingSerilog(builder.Configuration, ServiceName));
builder.Services.AddAiStockTradingObservability(builder.Configuration, ServiceName);

builder.Services.AddAiStockTradingHealthChecks();

// --- 取引判断のポートとサービス（Slice A）を配線する ---
builder.Services.AddSingleton<IClock, SystemClock>();
// IADR-0017: 実 LLM/実データが揃うまでの安全既定プレースホルダ（取引しない）。
builder.Services.AddSingleton<ILlmCompletionClient, PlaceholderLlmCompletionClient>();
// FR-07, IADR-0028: 確定済み日報方針は報告書サービス（#14）の GET /reports/daily-policy を同期照会して供給する。
// Reports:BaseUrl 未設定なら従来のプレースホルダ（no-op・取引しない）＝安全既定でゲート。設定時のみ実照会する。
// 選択は解決時に構成を読む（起動時読み取りだと WebApplicationFactory の構成上書きに追随しないため）。HttpClient は
// IHttpClientFactory 経由でハンドラをプールする。警告の 1 回化のためプレースホルダは singleton で共有する。
// 同期クリティカルパス（取引判断）に置くため短いタイムアウトを設定する（応答遅延でサイクルを長時間ブロックしない）。
builder.Services.AddHttpClient("reports", c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddSingleton<PlaceholderDailyPolicyProvider>();
builder.Services.AddScoped<IDailyPolicyProvider>(sp =>
{
    var baseUrl = sp.GetRequiredService<IConfiguration>()["Reports:BaseUrl"];
    // 未設定・不正 URI は安全既定（プレースホルダ＝取引しない）に倒す。
    if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        return sp.GetRequiredService<PlaceholderDailyPolicyProvider>();

    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("reports");
    http.BaseAddress = uri;
    return new HttpDailyPolicyProvider(http, sp.GetRequiredService<ILogger<HttpDailyPolicyProvider>>());
});
// FR-04/10, IADR-0029: サイジング文脈はリスク管理（#12）の GET /risk-controls/sizing-context を同期照会して供給する。
// RiskManagement:BaseUrl 未設定/不正 URI は従来プレースホルダ（既定値）＝安全既定でゲート。選択は解決時に構成を読む。
builder.Services.AddHttpClient("risk", c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddSingleton<PlaceholderSizingContextProvider>();
builder.Services.AddScoped<ISizingContextProvider>(sp =>
{
    var baseUrl = sp.GetRequiredService<IConfiguration>()["RiskManagement:BaseUrl"];
    if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        return sp.GetRequiredService<PlaceholderSizingContextProvider>();

    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("risk");
    http.BaseAddress = uri;
    return new HttpSizingContextProvider(http, sp.GetRequiredService<ILogger<HttpSizingContextProvider>>());
});
// FR-02, IADR-0023: 市場カレンダー（休場日ゲート）と定時サイクルの監視銘柄（暫定=構成ベース）。
builder.Services.AddSingleton<IMarketCalendar>(_ => new MarketCalendar(LoadHolidays(builder.Configuration)));
builder.Services.AddSingleton<IWatchlistProvider, ConfigurationWatchlistProvider>();
// FR-04, IADR-0039: 多数決・二段オーケストレーションの構成（Decision:*）。未設定なら Default（1 票・スクリーニング無効）
// ＝単発判断（IADR-0017）と等価＝現行挙動。実 LLM/モデル解決・回数の実値は後続（#23/#79 と連動）。
builder.Services.AddSingleton(DecisionOptionsLoader.FromConfiguration(builder.Configuration));
builder.Services.AddScoped<TradeDecisionService>();

// ADR-0003, IADR-0011, IADR-0023: MassTransit（RabbitMQ）。価格変動（イベント駆動）と収集完了（定時）の両系統を購読し、
// 取引判断で合流して TradeDecisionMade を発行する。
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<PriceMovementDetectedConsumer>();
    x.AddConsumer<InformationCollectedConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672");
        cfg.UseAiStockTradingRetry();
        cfg.ConfigureEndpoints(ctx);
    });
});

var app = builder.Build();

app.MapAiStockTradingHealthChecks();

app.Run();

// IADR-0023: 市場別の休場日を構成（TradeCycle:Holidays:<Market> = ["yyyy-MM-dd", ...]）から読み込む。既定は空（週末のみ）。
static IReadOnlyDictionary<Market, IReadOnlySet<DateOnly>> LoadHolidays(IConfiguration configuration)
{
    var result = new Dictionary<Market, IReadOnlySet<DateOnly>>();
    foreach (var market in Enum.GetValues<Market>())
    {
        var dates = configuration.GetSection($"TradeCycle:Holidays:{market}").Get<string[]>() ?? [];
        var set = new HashSet<DateOnly>();
        foreach (var d in dates)
        {
            if (DateOnly.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                set.Add(date);
        }

        if (set.Count > 0)
            result[market] = set;
    }

    return result;
}

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
