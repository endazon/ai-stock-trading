extern alias RiskManagementWorker;

using System.Net;
using System.Text.Json;
using RiskManagementWorker::RiskManagementService.Domain;
using RiskManagementWorker::RiskManagementService.Features.RiskManagement.GetOpenPositions;
using RiskManagementWorker::RiskManagementService.Features.RiskManagement.GetSizingContext;
using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// 🔴 FR-04, FR-10, #943, IADR-0390（PR #940 監査の同型）: 判断サービスがリスク管理の応答を**自前の型で読む**箇所の契約テスト。
//
// 手書きの JSON だけで試すと、送り手（リスク管理）の項目名を変えても両スイートとも緑のままになる
// （#943 の実測: `OpenPositionView.Symbol` → `Ticker` で TradeDecisionService.Tests 751 件・MarketMonitorService.Tests 157 件・
// ReportService.Tests 1101 件がすべて緑）。実行時はアダプタが既定値で逆シリアル化し、建玉を持っているのに「保有なし」を返す。
//
// そこで本テストは、
// 1. **送り手の本物の型**（`OpenPositionView` / `SizingContextView`）をリスク管理の Minimal API と同じ web 既定 JSON
//    （camelCase・列挙は数値）で直列化し、
// 2. **本番の Program.cs が組み立てたアダプタ**（`RiskManagement:BaseUrl` を与えて DI から解決。"risk" HttpClient の
//    一次ハンドラだけを差し替える）に読ませて、一致を表明する。
// 送り手の web 既定がそのまま通信路に出ていること（Program.cs が JSON 設定を変えていないこと）は、
// リスク管理側の T-10-805（`ReadContractWireFormatTests`）が本物の Program.cs で固定する。2 本で端から端までをつなぐ。
public class RiskManagementReadContractTests
{
    // リスク管理の Minimal API（Results.Ok）と同じ web 既定（camelCase・列挙は数値）。
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // 🔴 T-10-800: 約定済み保有（GET /risk-controls/open-positions）。送り手の項目名が変わると赤になる（変異注入で実測）。
    [Fact]
    public async Task 保有建玉は送り手の本物の型を直列化した応答から本番の配線のアダプタで読める()
    {
        IReadOnlyList<OpenPositionView> views =
        [
            new("AAPL", Market.UnitedStates, TradeSide.Buy, 3_378, 337.63m, 320.75m),
            new("7203", Market.Japan, TradeSide.Sell, 100, 2_500m, 2_600m),
        ];
        var handler = new RouteStub("/risk-controls/open-positions", JsonSerializer.Serialize(views, Web));
        using var factory = new Factory(handler);

        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IHeldPositionProvider>();
        provider.Should().BeOfType<HttpHeldPositionProvider>("本番の配線（RiskManagement:BaseUrl あり）が選ぶ実装を試す");

        var aapl = await provider.GetPositionAsync("AAPL", Market.UnitedStates);
        var toyota = await provider.GetPositionAsync("7203", Market.Japan);
        var none = await provider.GetPositionAsync("MSFT", Market.UnitedStates);
        var signed = await provider.GetSignedQuantityAsync("AAPL", Market.UnitedStates);

        aapl.Should().Be(new HeldPosition(3_378, 337.63m, 320.75m));
        toyota.Should().Be(new HeldPosition(-100, 2_500m, 2_600m));
        none.Should().Be(HeldPosition.None, "一致しない銘柄は「保有なし」（送り手の型のままでも区別が保たれる）");
        signed.Should().Be(3_378);
        handler.Paths.Should().OnlyContain(p => p == "/risk-controls/open-positions");
    }

    // 🔴 T-10-802: サイジング文脈（GET /risk-controls/sizing-context）。判断側の `SizingContext` は送り手と別の型であり、
    // 既存の写像テストは**受け手自身の型**を直列化していた（送り手の改名を検出できない）。連敗数・DD 比率は縮小係数
    // （`PositionSizer.GetSizeFactor`）の入力で、改名されると 0 に化けて**縮小が黙って外れる**（上限側への fail-open）。
    // 動作モードは 0＝InternalPaper に化ける。
    [Fact]
    public async Task サイジング文脈は送り手の本物の型を直列化した応答から本番の配線のアダプタで読める()
    {
        var limits = TradingDefaults.CreateRiskLimits() with { LosingStreakThreshold = 4, LosingStreakSizeFactor = 0.4m };
        // 位置引数で組む（送り手の項目名に依存するのは直列化の結果だけにする）。
        // 資金・段階残枠・日次残枠・連敗数・DD 比率・動作モード・上限・損切りの実行機構。
        var view = new SizingContextView(
            120_000m, 45_000m, 22_000m, 3, 0.07m, BrokerProvider.MoomooSimulate, limits,
            StopLossExecutionMethod.NoProtectiveStop);
        var handler = new RouteStub("/risk-controls/sizing-context", JsonSerializer.Serialize(view, Web));
        using var factory = new Factory(handler);

        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<ISizingContextProvider>();
        provider.Should().BeOfType<HttpSizingContextProvider>("本番の配線（RiskManagement:BaseUrl あり）が選ぶ実装を試す");

        var context = await provider.GetContextAsync();

        context.Capital.Should().Be(120_000m);
        context.StageCapitalRemaining.Should().Be(45_000m);
        context.DailyOrderRemaining.Should().Be(22_000m);
        context.ConsecutiveLosses.Should().Be(3, "0 に化けると連敗時の縮小が外れる");
        context.DrawdownRatio.Should().Be(0.07m, "0 に化けると DD 時の縮小が外れる");
        context.Mode.Should().Be(BrokerProvider.MoomooSimulate, "0 に化けると InternalPaper を名乗る");
        context.Limits.Should().Be(limits);
        context.StopLossMethod.Should().Be(StopLossExecutionMethod.NoProtectiveStop);
        handler.Paths.Should().OnlyContain(p => p == "/risk-controls/sizing-context");
    }

    // 指定の 1 経路にだけ 200 と本文を返す。他の経路は 404（＝アダプタは不明・安全既定へ倒す）。
    private sealed class RouteStub(string path, string body) : HttpMessageHandler
    {
        private readonly List<string> _paths = [];

        public IReadOnlyList<string> Paths
        {
            get
            {
                lock (_paths)
                    return [.. _paths];
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requested = request.RequestUri?.AbsolutePath ?? string.Empty;
            lock (_paths)
                _paths.Add(requested);
            return Task.FromResult(requested == path
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    // 本番の Program.cs をそのまま起こし、"risk" HttpClient の一次ハンドラだけを差し替える（アダプタの選択・BaseAddress・
    // 委譲ハンドラの連鎖は本番の配線のまま）。
    private sealed class Factory(HttpMessageHandler riskHandler) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:ConnectionString"] = "amqp://localhost",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["RiskManagement:BaseUrl"] = "http://risk",
            }));
            builder.ConfigureServices(services =>
                // ADR-0013, IADR-0129, #354: 実 RabbitMQ を避けて Wolverine の外部トランスポートを無効化する。
                services.DisableAllExternalWolverineTransports());
            builder.ConfigureTestServices(services =>
                services.AddHttpClient("risk").ConfigurePrimaryHttpMessageHandler(() => riskHandler));
        }
    }
}
