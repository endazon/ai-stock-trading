using System.Net;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Worker.Composable.Adapters;
using FluentAssertions;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.TradeDecision.Worker.Tests;

// FR-04, FR-05, FR-10, #292, IADR-0119: 保有建玉の同期照会（GET /risk-controls/open-positions）。
// 中核の契約は「空配列＝0（保有なし）／失敗＝null（不明）」の厳格な区別。
// 失敗を 0 へ倒すと「保有していない」と誤断定し、裸の新規売りを通してしまう。
public class HttpHeldPositionProviderTests
{
    private static HttpHeldPositionProvider Provider(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://risk") },
            NullLogger<HttpHeldPositionProvider>.Instance);

    // OpenPositionView（RiskManagement）の web 既定 JSON（camelCase・列挙は数値）。
    private const string TwoPositions = """
        [
          {"symbol":"AAPL","market":1,"side":0,"quantity":4072,"entryPrice":20.5,"stopLossPrice":19.0},
          {"symbol":"7203","market":0,"side":1,"quantity":100,"entryPrice":2500,"stopLossPrice":2600}
        ]
        """;

    [Fact]
    public async Task ロング建玉は正の数量で返る()
    {
        var handler = new StubHandler(HttpStatusCode.OK, TwoPositions);

        var held = await Provider(handler).GetSignedQuantityAsync("AAPL", Market.UnitedStates);

        held.Should().Be(4072);
        handler.LastPath.Should().Be("/risk-controls/open-positions");
    }

    [Fact]
    public async Task ショート建玉は負の数量で返る()
    {
        var held = await Provider(new StubHandler(HttpStatusCode.OK, TwoPositions))
            .GetSignedQuantityAsync("7203", Market.Japan);

        held.Should().Be(-100);
    }

    [Fact]
    public async Task 一覧に無い銘柄は保有なしのゼロ()
    {
        var held = await Provider(new StubHandler(HttpStatusCode.OK, TwoPositions))
            .GetSignedQuantityAsync("MSFT", Market.UnitedStates);

        held.Should().Be(0, "不明（null）ではなく保有なし（0）");
    }

    [Fact]
    public async Task 同一コードの別市場は数えない()
    {
        var held = await Provider(new StubHandler(HttpStatusCode.OK, TwoPositions))
            .GetSignedQuantityAsync("7203", Market.UnitedStates);

        held.Should().Be(0);
    }

    [Fact]
    public async Task 空配列は保有なしのゼロ()
    {
        // 空列は「建玉が 1 つも無い」という観測事実。null（不明）と取り違えない。
        var held = await Provider(new StubHandler(HttpStatusCode.OK, "[]"))
            .GetSignedQuantityAsync("AAPL", Market.UnitedStates);

        held.Should().Be(0);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task 非2xx_は不明(HttpStatusCode status)
    {
        var held = await Provider(new StubHandler(status, "")).GetSignedQuantityAsync("AAPL", Market.UnitedStates);

        held.Should().BeNull("失敗を 0 へ倒すと裸の新規売りを通してしまう");
    }

    [Fact]
    public async Task 不正な応答は不明()
    {
        var held = await Provider(new StubHandler(HttpStatusCode.OK, "null"))
            .GetSignedQuantityAsync("AAPL", Market.UnitedStates);

        held.Should().BeNull();
    }

    [Fact]
    public async Task 例外は不明()
    {
        var held = await Provider(new ThrowingHandler()).GetSignedQuantityAsync("AAPL", Market.UnitedStates);

        held.Should().BeNull();
    }

    // --- 配線（RiskManagement:BaseUrl の有無で切り替わる） ---

    [Fact]
    public void BaseUrl未設定は安全既定のNoOp()
    {
        using var factory = new Factory(riskBaseUrl: null);
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHeldPositionProvider>()
            .Should().BeOfType<AiStockTrading.TradeDecision.Application.Adapters.NoOpHeldPositionProvider>();
    }

    [Fact]
    public void BaseUrl設定時はリスク管理を同期照会するHttp実装()
    {
        using var factory = new Factory(riskBaseUrl: "http://risk");
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHeldPositionProvider>()
            .Should().BeOfType<HttpHeldPositionProvider>();
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("接続できません");
    }

    private sealed class Factory(string? riskBaseUrl) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["RabbitMq:ConnectionString"] = "amqp://localhost",
                    ["Otlp:Endpoint"] = "http://localhost:4317",
                };
                if (riskBaseUrl is not null)
                    settings["RiskManagement:BaseUrl"] = riskBaseUrl;
                cfg.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBusControl>();
                services.AddMassTransitTestHarness(x => x.AddConsumer<Composable.Steps.PriceMovementDetectedConsumer>());
            });
        }
    }
}
