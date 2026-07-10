using AiStockTrading.TradeDecision.Application.Adapters;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Application.Services;
using AiStockTrading.TradeDecision.Worker.Composable.Adapters;
using AiStockTrading.TradeDecision.Worker.Composable.Steps;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using MassTransit;
using Serilog;

const string ServiceName = "ai-stock-trading.trade-decision-service";

// #11 Slice A, IADR-0013/0017: ヘルスチェックの HTTP サーフェスのため WebApplication を用いる。
// PriceMovementDetected 購読は MassTransit コンシューマとして稼働する。判断はステートレス（DB なし）。
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
builder.Services.AddScoped<TradeDecisionService>();

// ADR-0003, IADR-0011: MassTransit（RabbitMQ）。PriceMovementDetected を購読し TradeDecisionMade を発行する。
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<PriceMovementDetectedConsumer>();
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

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
