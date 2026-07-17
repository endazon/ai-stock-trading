using AiStockTrading.Configuration.Client.Composable.Steps;
using AiStockTrading.Configuration.Client.Foundation.Extensions;
using AiStockTrading.CostControl.Application.Adapters;
using AiStockTrading.CostControl.Application.Ports;
using AiStockTrading.CostControl.Worker.Composable.Adapters;
using AiStockTrading.CostControl.Worker.Composable.Retention;
using AiStockTrading.CostControl.Worker.Composable.Steps;
using AiStockTrading.CostControl.Worker.Foundation.Endpoints;
using AiStockTrading.CostControl.Worker.Foundation.Persistence;
using AiStockTrading.Shared.Contracts.Operations;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Serilog;
using AppSvc = AiStockTrading.CostControl.Application.Services.CostControlService;

const string ServiceName = "ai-stock-trading.cost-control-service";

// #23 Slice A, NFR（費用）, IADR-0027: 費用統制サービス。LLM の月次費用計上と上限に対する間隔延長/停止判定（Keycloak 認可）と
// ヘルスチェックのため WebApplication を用いる。しきい値の上方遷移時に CostThresholdReached を発行する（MassTransit）。
//
// IADR-0013: 本 Program.cs の standalone 配線は dev/test/CI のローカル単体実行のためのもの。本番は platform 統合（#22）で置換。
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((_, logConfig) =>
    logConfig.ConfigureAiStockTradingSerilog(builder.Configuration, ServiceName));
builder.Services.AddAiStockTradingObservability(builder.Configuration, ServiceName);

// ADR-0007: Keycloak 認証（費用計上・照会）。
builder.Services.AddAiStockTradingAuth(builder.Configuration);

// ADR-0001（Database per Service）, IADR-0027: 費用統制専有 DB（cost_control_svc）。
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=postgres;Port=5432;Database=cost_control_svc;Username=ai;Password=ai";
builder.Services.AddDbContext<CostControlDbContext>(opt => opt.UseNpgsql(connStr));

builder.Services.AddAiStockTradingHealthChecks()
    .AddNpgSql(connStr, tags: ["ready"]);

// 列挙（CostCategory/State）を文字列で送受信する。
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddSingleton<IClock, SystemClock>();

// FR-17, #139, IADR-0065: 月次費用上限はバージョン付き全体前提条件（#19・設定サービス）から解決する。
// 利用者が設定サービスで上限を変更すると、しきい値判定（80%/100%）へ反映される。
// 安全既定（IADR-0063 決定 6）: `Configuration:BaseUrl` 未設定なら HTTP は構築されず前提条件の既定値へ倒れる
// （＝従来の DefaultCostLimitsProvider と同じ挙動）。よって既定ビルド/CI は外部接続なしで成立する。
builder.Services.AddAiStockTradingAssumptions(builder.Configuration);
builder.Services.AddSingleton<ICostLimitsProvider, AssumptionsCostLimitsProvider>();
builder.Services.AddScoped<ICostLedger, EfCostLedger>();
// #79, IADR-0055 決定5: LlmCostIncurred の二重計上を防ぐ重複排除ストア（費用台帳とは別テーブル）。
builder.Services.AddScoped<IProcessedMessageStore, EfProcessedMessageStore>();
builder.Services.AddScoped<AppSvc>();

// NFR（運用）, #137, IADR-0059: processed_messages の保持期間パージ（既定無効。Retention:Enabled=true で有効化）。
// 保持 90 日・下限 7 日クランプにより、再配信の猶予内にある行は決してパージされない。
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection(RetentionOptions.SectionName));
builder.Services.AddHostedService<ProcessedMessageRetentionService>();

// IADR-0011/0027: MassTransit（RabbitMQ）。しきい値到達時の CostThresholdReached 発行に用いる。
// #79, IADR-0055 決定1: さらに LlmCostIncurred を購読し LLM 費用を計上する（HTTP /costs/record は OwnerOnly のため）。
// #139, IADR-0065 決定4: AssumptionsChanged を購読し、上限キャッシュを無効化して新しい版へ追随する
// （購読が外れると TTL 既定 5 分まで上限変更が反映されない）。
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<LlmCostIncurredConsumer>();
    x.AddConsumer<AssumptionsChangedConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672");
        cfg.UseAiStockTradingRetry();
        cfg.ConfigureEndpoints(ctx);
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CostControlDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

app.UseAiStockTradingMiddleware();
app.MapAiStockTradingHealthChecks();

// NFR（費用）: 費用計上・統制判定・費用レビュー（利用者/サービス）。
app.MapCostControlEndpoints();

app.Run();

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
