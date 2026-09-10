using BacktestService.Features.Backtest;
using BacktestService.Features.Backtest.EvaluateStage0Gate;
using BacktestService.Hosted;
using BacktestService.Infrastructure.ExternalServices;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;
using Microsoft.Extensions.Options;
using Serilog;
using Wolverine;

const string ServiceName = "ai-stock-trading.backtest-service";

// FR-15, FR-20, ADR-0008, #208, IADR-0105, #688, IADR-0310: バックテストサービスのホスト。
//
// 本ホストは**実過去データ源の合成**と、**Stage 0 判定の定時駆動＋verdict の発行**を持つ。
// Stage 0 判定そのもの（DSR/PBO/ウォークフォワード等）は純ドメイン（BacktestService.Domain）が持つ。
//   - 定時駆動は Hosted/Stage0EvaluationService（**既定は無効**＝fail-safe。IADR-0310 決定1）
//   - 評価対象は構成 `Backtest:Stage0:Strategy` で選ぶ（#632, IADR-0318 決定3）。
//     `recorded-replay` は計画 ADR-0033（2026-09-05 裁定）の「AI 判断そのもの（記録・再生）」であり、
//     **記録が構成と整合するときだけ**本物の判定器（Stage0GateService）へ進む。既定の `placeholder` は
//     駆動経路の確認用で、**その verdict は不合格固定であり go-live の判断材料にはならない**（IADR-0310 決定3）。
//   - 🔴 **どちらの経路にも合格を作る口は無い。** 合格を出せるのは Stage0GateService の 7 条件だけである。
//   - 実 RabbitMQ / 実過去データを用いた E2E は #82（IADR-0089 で整理済・IADR-0310 決定5 で維持）
//
// IADR-0013: 本 Program.cs の standalone 配線は dev/test/CI のローカル単体実行のためのもの。本番は platform 統合（#22）で置換。
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((_, logConfig) =>
    logConfig.ConfigureAiStockTradingSerilog(builder.Configuration, ServiceName));
builder.Services.AddAiStockTradingObservability(builder.Configuration, ServiceName);

// DB は持たない（BacktestService は永続化を持たない）。stateless のため /health/ready は起動直後に healthy
// （IADR-0049 決定 3）。**メッセージバスは持つ**（下の Wolverine 配線。#688 / IADR-0310 決定4）。
builder.Services.AddAiStockTradingHealthChecks();

// ADR-0013, IADR-0129, #688, IADR-0310 決定4: Wolverine（RabbitMQ）。**発行専用**（ハンドラは持たない）。
// キュー名・fan-out・再試行・DLQ の規則は共通ヘルパに閉じている（サービス側でトポロジを選ばない）。
builder.Host.UseWolverine(opts =>
    opts.UseAiStockTradingRabbitMq(ServiceName, builder.Configuration["RabbitMq:ConnectionString"]));

// 公開する HTTP 面はヘルスチェックと実効構成の自己申告のみで、いずれも無認可（メッシュ内部限定）である。
// よって Keycloak 認証（AddAiStockTradingAuth）は登録しない。ただし共通ミドルウェア
// （UseAiStockTradingMiddleware）が UseAuthentication/UseAuthorization を含むため、その依存だけを満たす
// （スキーム未登録＝認証は素通り。保護対象のエンドポイントが無いため素通りで問題ない）。
// 認可を要する API を足す際は AddAiStockTradingAuth へ差し替えること（platform ADR-0004）。
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();

// FR-15, ADR-0004, ADR-0023, #208, IADR-0105, IADR-0157: 実過去データ源（Stooq / moomoo）の構成。
// 既定・空・"none"・未知 provider・構成不備はすべて no-op＝**外部へ 1 リクエストも出さない**。
//
// **既定は none のままである**（ADR-0023 決定5 は moomoo を採用したが、「実装側で確認を要する 2 点」
// （取得枠の単位と回復周期／前復権と ADR-0016 決定14 の費用モデルの整合）を本決定の前提としており、
// いずれも実 OpenD を要して未了である）。moomoo は**明示的に構成したときだけ**使う。
builder.Services.Configure<BarDataOptions>(builder.Configuration.GetSection(BarDataOptions.SectionName));
builder.Services.AddHttpClient(BarDataHttpClientName);

// ADR-0023 決定5, IADR-0157: OpenD への接続は provider=moomoo のときだけ作る（それ以外では 1 本も張らない）。
var barDataOptions = builder.Configuration.GetSection(BarDataOptions.SectionName).Get<BarDataOptions>();
var barDataProvider = HistoricalBarSourceFactory.ResolveProvider(barDataOptions);
if (barDataProvider == HistoricalBarSourceFactory.Moomoo)
{
    builder.Services.AddSingleton<IMoomooHistoryKLineClient>(sp => new MMApiMoomooHistoryKLineClient(
        sp.GetRequiredService<IOptions<BarDataOptions>>().Value.Moomoo,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<MMApiMoomooHistoryKLineClient>()));
}

builder.Services.AddSingleton<IHistoricalBarSource>(sp =>
{
    var options = sp.GetRequiredService<IOptions<BarDataOptions>>().Value;
    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(BarDataHttpClientName);
    return HistoricalBarSourceFactory.Create(
        options,
        httpClient,
        TimeProvider.System,
        sp.GetRequiredService<ILoggerFactory>(),
        sp.GetService<IMoomooHistoryKLineClient>()); // moomoo 時のみ登録済み
});

// FR-15, FR-20, ADR-0008, ADR-0033, #688, IADR-0310 決定1: Stage 0 判定の定時駆動（**既定は無効**）。
// 常駐は常に登録し、有効・無効の判定は ExecuteAsync が持つ（無効なら 1 度も巡回せず、外部要求も発行も起きない）。
// 評価には過去データの取得（scoped スコープからの解決）と Wolverine の IMessageBus が要るため、
// 上の 2 つの登録より後に置く。
builder.Services.Configure<Stage0EvaluationOptions>(
    builder.Configuration.GetSection(Stage0EvaluationOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
// FR-04, FR-15, ADR-0033 決定2, #632, IADR-0318: AI 判断の記録の供給。
// **既定は「記録なし」**（NoStage0DecisionRecordSource＝ファイルも読まない）。パスを明示したときだけ実読み込みに
// なる。記録が無ければ記録再生戦略は評価対象を持たず、合格 verdict は出ない（fail-closed）。
builder.Services.AddScoped<IStage0DecisionRecordSource>(sp =>
{
    var recordingPath = sp.GetRequiredService<IConfiguration>()[$"{Stage0EvaluationOptions.SectionName}:Recording:Path"];
    return string.IsNullOrWhiteSpace(recordingPath)
        ? new NoStage0DecisionRecordSource()
        : new FileStage0DecisionRecordSource(
            recordingPath, sp.GetRequiredService<ILogger<FileStage0DecisionRecordSource>>());
});
builder.Services.AddHostedService<Stage0EvaluationService>();
var stage0DriverEnabled = builder.Configuration.GetSection(Stage0EvaluationOptions.SectionName)
    .Get<Stage0EvaluationOptions>()?.Enabled == true;

// ADR-0001, FR-15, #22 受け入れ基準③: 実効構成（選択中ポート実装）の自己申告。
// 「有効化したつもりで効いていない」を、メッシュ内部から provider 名で確認できるようにする。
// #688, IADR-0310 決定1: 定時駆動の実効状態も同じ手段で確認できるようにする（構成と挙動の乖離を見せる）。
// #632, IADR-0318: 評価対象（戦略）の実効値も自己申告に載せる。**綴り違いで既定へ倒れていることを
// メッシュ内部から確認できる**ようにするためであり、駆動の有効・無効と同じ手段で見えることに意味がある。
var stage0Options = builder.Configuration.GetSection(Stage0EvaluationOptions.SectionName)
    .Get<Stage0EvaluationOptions>() ?? new Stage0EvaluationOptions();
builder.Services.AddAiStockTradingIntrospection(builder.Configuration, ServiceName, b => b
    .AddPort("historical-bar-data", barDataProvider)
    .AddPort("stage0-driver", stage0DriverEnabled ? "enabled" : "disabled")
    .AddPort("stage0-strategy", stage0Options.ResolveStrategy()));

var app = builder.Build();

// FR-15, ADR-0023 決定5, IADR-0060 決定5, IADR-0157, #382: **構成不備を起動時に落とすため、ここで強制解決する。**
//
// `MMApiMoomooHistoryKLineClient` のコンストラクタが `MoomooBarDataPreflight` を呼ぶが、**それだけでは
// 起動時に効かない**。`AddSingleton<T>(factory)` で登録したシングルトンは遅延生成であり、組み込み DI は
// `builder.Build()` では構築しない。BacktestService には発注経路の `BrokerAvailabilityProbeService` に
// あたる eager な消費者が無く（`IHistoricalBarSource` を解決するのは定時駆動だが、**既定は無効**であり、
// 有効でも解決は初回巡回まで遅延する）、
// **鍵のマウントを誤ってもプロセスは正常に起動し続け、失敗は初回のバー取得まで顕在化しない。**
// 例外の種類と文言が改善されても、**表面化のタイミングという核心が変わらなければ preflight の意味が無い。**
//
// 接続は張らない（`EnsureConnectedAsync` は初回要求まで遅延する）。ここで走るのは構成の検査だけである。
if (barDataProvider == HistoricalBarSourceFactory.Moomoo)
{
    _ = app.Services.GetRequiredService<IMoomooHistoryKLineClient>();
}

app.UseAiStockTradingMiddleware();
app.MapAiStockTradingHealthChecks();
app.MapAiStockTradingIntrospection();

app.Run();

// 過去データ取得用の名前付き HttpClient（タイムアウト等の調整点はここに集約する）。
public partial class Program
{
    public const string BarDataHttpClientName = "backtest-bar-data";
}
