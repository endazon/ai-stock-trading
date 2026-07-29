using AiStockTrading.Notification.Application.Ports;
using AiStockTrading.Notification.Application.Services;
using AiStockTrading.Notification.Worker.Composable.Adapters;
using AiStockTrading.Notification.Worker.Composable.Steps;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;
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
// ADR-0001, FR-15, #22 受け入れ基準③: 実効構成（有効な段=宣言由来・選択中ポート実装・構成バージョン）の自己申告。
// メッシュ内部限定エンドポイント GET /internal/introspection（無認可・ネットワーク分離が防御）。
builder.Services.AddAiStockTradingIntrospection(builder.Configuration, ServiceName, b => b
    .AddPort("notifier", string.IsNullOrWhiteSpace(builder.Configuration["Notifications:Provider"]) ? "noop" : builder.Configuration["Notifications:Provider"]!)
    .AddPortFromBaseUrl("risk-control", builder.Configuration["RiskManagement:BaseUrl"], "http", "placeholder"));

// FR-09, IADR-0020: 送信手段の選択（安全既定 no-op）。実 Discord 送信は Notifications:Provider=discord-webhook で明示有効化する。
builder.Services.AddHttpClient();
builder.Services.AddSingleton<INotificationSender>(sp => NotificationSenderFactory.Create(
    builder.Configuration["Notifications:Provider"],
    builder.Configuration["Notifications:Discord:WebhookUrl"],
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("discord"),
    sp.GetRequiredService<ILoggerFactory>()));

// FR-14, UC-06, IADR-0062: Discord Bot（双方向）。既定は無効（Gateway に接続しない）。
// Notifications:Discord:Bot:Enabled=true ＋ Token ＋ 多層認証の設定が揃った時のみ実接続する。
var discordBotOptions = DiscordBotOptionsReader.Read(builder.Configuration);
builder.Services.AddSingleton(discordBotOptions);

// kill switch は Risk の OwnerOnly エンドポイントを呼ぶ（Risk 側は無改修）。IADR-0051 の s2s トークン
// （trading-service）では 403 のため、Bot 専用の owner マップ機密クライアントのトークンを付与する（IADR-0062 決定4）。
// RiskManagement:BaseUrl 未設定/不正 URI は BaseAddress 未設定＝呼び出し失敗（Succeeded=false）に倒す。
builder.Services.AddHttpClient("risk-kill-switch", c => c.Timeout = TimeSpan.FromSeconds(5))
    .AddDiscordOwnerToken(builder.Configuration);
builder.Services.AddSingleton<IKillSwitchController>(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("risk-kill-switch");
    var baseUrl = builder.Configuration["RiskManagement:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        http.BaseAddress = uri;

    return new HttpKillSwitchController(http, sp.GetRequiredService<ILogger<HttpKillSwitchController>>());
});

builder.Services.AddSingleton<KillSwitchCommandHandler>();

// FR-10, FR-14, UC-06/07, ADR-0009, IADR-0075: 一時停止/再開・状態照会。kill switch と同じく Risk の OwnerOnly
// エンドポイントを owner マップ機密クライアントのトークンで呼ぶ（trading-service では 403）。Risk 側は無改修。
builder.Services.AddHttpClient("risk-pause", c => c.Timeout = TimeSpan.FromSeconds(5))
    .AddDiscordOwnerToken(builder.Configuration);
builder.Services.AddSingleton<IPauseController>(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("risk-pause");
    var baseUrl = builder.Configuration["RiskManagement:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        http.BaseAddress = uri;

    return new HttpPauseController(http, sp.GetRequiredService<ILogger<HttpPauseController>>());
});
builder.Services.AddSingleton<PauseCommandHandler>();

// FR-20, FR-14, UC-06, ADR-0008, IADR-0070/0081: 段階ゲート（#20）の承認遷移・撤退評価・現況照会。kill switch / pause と
// 同じく Risk の OwnerOnly エンドポイントを owner マップ機密クライアントのトークンで呼ぶ（trading-service では 403）。Risk 側は無改修。
builder.Services.AddHttpClient("risk-stage-gate", c => c.Timeout = TimeSpan.FromSeconds(5))
    .AddDiscordOwnerToken(builder.Configuration);
builder.Services.AddSingleton<IStageGateController>(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("risk-stage-gate");
    var baseUrl = builder.Configuration["RiskManagement:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        http.BaseAddress = uri;

    return new HttpStageGateController(http, sp.GetRequiredService<ILogger<HttpStageGateController>>());
});
builder.Services.AddSingleton<StageGateCommandHandler>();

builder.Services.AddSingleton<IDiscordBotGateway>(sp => DiscordBotGatewayFactory.Create(
    discordBotOptions,
    sp.GetRequiredService<KillSwitchCommandHandler>(),
    sp.GetRequiredService<PauseCommandHandler>(),
    sp.GetRequiredService<StageGateCommandHandler>(),
    sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddHostedService<DiscordBotHostedService>();

// IADR-0011/0020: MassTransit（RabbitMQ）。取引実行・リスク統制発動のイベントを購読して通知する。
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<OrderExecutedNotificationConsumer>();
    x.AddConsumer<OrderRejectedNotificationConsumer>();
    x.AddConsumer<StopLossTriggeredNotificationConsumer>();
    // FR-17: 設定管理（#19）の前提条件変更通知。
    x.AddConsumer<AssumptionsChangedNotificationConsumer>();
    // FR-07/FR-09: 報告書（#14）の確定通知。
    x.AddConsumer<ReportConfirmedNotificationConsumer>();
    // FR-06/07/09/#280, IADR-0116: 報告書ドラフトの提示（確定依頼）の通知。
    x.AddConsumer<ReportDraftPresentedNotificationConsumer>();
    // NFR（費用）/FR-09: 費用統制（#23）のしきい値通知。
    x.AddConsumer<CostThresholdReachedNotificationConsumer>();
    // FR-20/FR-09: 撤退基準到達（撤退の定期評価ドライバ #166）の通知。
    x.AddConsumer<WithdrawalTriggeredNotificationConsumer>();
    // UC-01/FR-09/FR-07: 日報未確定による取引スキップ（取引判断 #11・#210）の確定を促す通知。
    x.AddConsumer<DailyPolicyUnconfirmedNotificationConsumer>();
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
app.MapAiStockTradingIntrospection();

app.Run();

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
