using AiStockTrading.Backtest.Application;
using AiStockTrading.Backtest.Infrastructure.Composable.Adapters;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;
using Microsoft.Extensions.Options;
using Serilog;

const string ServiceName = "ai-stock-trading.backtest-service";

// FR-15, FR-20, ADR-0008, #208, IADR-0105: バックテストサービスのホスト。
//
// 本ホストの責務は**実過去データ源の合成**に限る。Stage 0 判定そのもの（DSR/PBO/ウォークフォワード等）は
// 純ドメイン（BacktestService.Domain / .Application）が持ち、定時実行・verdict の実 publish は行わない。
//   - 本番戦略（IBacktestStrategy 実装）はまだ存在せず、実行する対象が無い
//   - BacktestEvaluated の実 publish と実コンテナ E2E は #82（IADR-0089 で整理済）
// したがって現時点では「no-op 既定の過去データ源を構成から解決し、実効構成を自己申告する」ホストである。
// 定時トリガ・publish を足す場所は本ホストであり、それらは別 issue で載せる。
//
// IADR-0013: 本 Program.cs の standalone 配線は dev/test/CI のローカル単体実行のためのもの。本番は platform 統合（#22）で置換。
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((_, logConfig) =>
    logConfig.ConfigureAiStockTradingSerilog(builder.Configuration, ServiceName));
builder.Services.AddAiStockTradingObservability(builder.Configuration, ServiceName);

// DB もメッセージバスも持たない（BacktestService は永続化を持たず、イベント発行は #82）。
// stateless のため /health/ready は起動直後に healthy（IADR-0049 決定 3）。
builder.Services.AddAiStockTradingHealthChecks();

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

// ADR-0001, FR-15, #22 受け入れ基準③: 実効構成（選択中ポート実装）の自己申告。
// 「有効化したつもりで効いていない」を、メッシュ内部から provider 名で確認できるようにする。
builder.Services.AddAiStockTradingIntrospection(builder.Configuration, ServiceName, b => b
    .AddPort("historical-bar-data", barDataProvider));

var app = builder.Build();

// FR-15, ADR-0023 決定5, IADR-0060 決定5, IADR-0157, #382: **構成不備を起動時に落とすため、ここで強制解決する。**
//
// `MMApiMoomooHistoryKLineClient` のコンストラクタが `MoomooBarDataPreflight` を呼ぶが、**それだけでは
// 起動時に効かない**。`AddSingleton<T>(factory)` で登録したシングルトンは遅延生成であり、組み込み DI は
// `builder.Build()` では構築しない。BacktestService には発注経路の `BrokerAvailabilityProbeService` に
// あたる eager な消費者が無く（本番戦略が未実装で `IHistoricalBarSource` を解決する実消費者が無い）、
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
