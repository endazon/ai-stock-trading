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
builder.Services.AddSingleton<IDailyPolicyProvider, PlaceholderDailyPolicyProvider>();
builder.Services.AddSingleton<ISizingContextProvider, PlaceholderSizingContextProvider>();
// FR-02, IADR-0023: 市場カレンダー（休場日ゲート）と定時サイクルの監視銘柄（暫定=構成ベース）。
builder.Services.AddSingleton<IMarketCalendar>(_ => new MarketCalendar(LoadHolidays(builder.Configuration)));
builder.Services.AddSingleton<IWatchlistProvider, ConfigurationWatchlistProvider>();
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
