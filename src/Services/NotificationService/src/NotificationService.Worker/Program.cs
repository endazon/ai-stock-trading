using AiStockTrading.Notification.Application.Ports;
using AiStockTrading.Notification.Worker.Composable.Adapters;
using AiStockTrading.Notification.Worker.Composable.Steps;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using MassTransit;
using Serilog;

const string ServiceName = "ai-stock-trading.notification-service";

// #15 Slice A, FR-09, IADR-0020: 通知サービス。取引実行・リスク統制発動のイベントを購読し Discord へ一方向通知する。
// ヘルスチェックの HTTP サーフェスのため WebApplication を用いる（DB・認可なし）。実 Discord 送信は既定で無効（安全既定）。
//
// IADR-0013: 本 Program.cs の standalone 配線（MassTransit/RabbitMQ を shim 経由で組む部分）は dev/test/CI での
// ローカル単体実行のためのもの。本番は platform 統合（#22）で共通基盤に置き換わる。
var builder = WebApplication.CreateBuilder(args);

// IADR-0011: 可観測性（Serilog + OTel）。
builder.Services.AddSerilog((_, logConfig) =>
    logConfig.ConfigureAiStockTradingSerilog(builder.Configuration, ServiceName));
builder.Services.AddAiStockTradingObservability(builder.Configuration, ServiceName);

// liveness ヘルスチェック（DB を持たないため readiness の外部依存チェックは無し）。
builder.Services.AddAiStockTradingHealthChecks();

// FR-09, IADR-0020: 送信手段の選択（安全既定 no-op）。実 Discord 送信は Notifications:Provider=discord-webhook で明示有効化する。
builder.Services.AddHttpClient();
builder.Services.AddSingleton<INotificationSender>(sp => NotificationSenderFactory.Create(
    builder.Configuration["Notifications:Provider"],
    builder.Configuration["Notifications:Discord:WebhookUrl"],
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("discord"),
    sp.GetRequiredService<ILoggerFactory>()));

// IADR-0011/0020: MassTransit（RabbitMQ）。取引実行・リスク統制発動のイベントを購読して通知する。
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<OrderExecutedNotificationConsumer>();
    x.AddConsumer<OrderRejectedNotificationConsumer>();
    x.AddConsumer<StopLossTriggeredNotificationConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672");
        // 送信失敗を含む一時的失敗は再試行し、継続失敗はデッドレターへ退避する（回復性）。
        cfg.UseAiStockTradingRetry();
        cfg.ConfigureEndpoints(ctx);
    });
});

var app = builder.Build();

// /health/live・/health/ready。
app.MapAiStockTradingHealthChecks();

app.Run();

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
