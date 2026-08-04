using AiStockTrading.Notification.Application.Ports;
using AiStockTrading.Notification.Application.Services;
using AiStockTrading.Notification.Infrastructure.Composable.Adapters;
using AiStockTrading.Notification.Infrastructure.Composable.Steps;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;
using Serilog;
using Wolverine;

const string ServiceName = "ai-stock-trading.notification-service";

// #15 Slice A, FR-09, IADR-0020: 通知サービス。取引実行・リスク統制発動のイベントを購読し Discord へ一方向通知する。
// ヘルスチェックの HTTP サーフェスのため WebApplication を用いる（DB・認可なし）。実 Discord 送信は既定で無効（安全既定）。
//
// IADR-0013: 本 Program.cs の standalone 配線（Wolverine/RabbitMQ を shim 経由で組む部分）は dev/test/CI での
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
// #289: Webhook URL は資格情報のため、送信専用クライアントの既定リクエストログ（URI を平文で出す）を抑止する。
builder.Services.AddDiscordWebhookHttpClient();
builder.Services.AddSingleton<INotificationSender>(sp => NotificationSenderFactory.Create(
    builder.Configuration["Notifications:Provider"],
    builder.Configuration["Notifications:Discord:WebhookUrl"],
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(DiscordWebhookHttpClientExtensions.ClientName),
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

// ADR-0013, IADR-0129, #354: Wolverine（RabbitMQ）。取引実行・リスク統制発動のイベントを購読して通知する。
// ハンドラは明示登録ではなくアセンブリ走査で発見されるため、ハンドラを持つアセンブリ（Infrastructure）を明示する。
// 購読するのは NotificationHandlers.cs の 10 種（取引実行・拒否・損切り／前提条件変更 FR-17／報告書の確定・提示
// FR-06/07/09・IADR-0116／費用しきい値 NFR／撤退基準到達 FR-20／日報未確定 #210／建玉乖離 #292・IADR-0118）。
// **Wolverine ではハンドラの発見漏れ＝静かな未通知**になる（明示登録が無いため「登録し忘れ」は
// ハンドラのアセンブリを渡し忘れる形で起こる）。10 種が扱われることは Infrastructure のテストが固定する。
// 送信失敗を含む一時的失敗の再試行と <queue>_error への退避は共通ヘルパに閉じている（IADR-0129 決定 5）。
builder.Host.UseWolverine(opts => opts.UseAiStockTradingRabbitMq(
    ServiceName,
    builder.Configuration["RabbitMq:ConnectionString"],
    typeof(OrderExecutedNotificationHandler).Assembly));

var app = builder.Build();

// /health/live・/health/ready。
app.MapAiStockTradingHealthChecks();
app.MapAiStockTradingIntrospection();

app.Run();

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
