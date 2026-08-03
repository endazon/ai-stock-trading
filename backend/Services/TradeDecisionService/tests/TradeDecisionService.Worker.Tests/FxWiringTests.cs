using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;
using ShimIntrospection = AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection.IntrospectionExtensions;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Worker.Composable.Adapters;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AiStockTrading.TradeDecision.Worker.Tests;

// FR-10, FR-17, #257, IADR-0107: 為替レート源の配線を固定する。配線が外れると、構成で有効化したつもりのレート源が
// 黙って no-op のままになり、外貨建て銘柄が恒久的に見送られる（＝症状が「取引しない」なので気づきにくい）。
public class FxWiringTests
{
    [Fact]
    public void 既定は実接続しないno_opのレート源()
    {
        using var factory = new Factory();
        _ = factory.CreateClient();

        factory.Services.GetRequiredService<IFxRateSource>().Should().BeOfType<NoOpFxRateSource>();
    }

    [Fact]
    public void fred指定かつAPIキーありで実レート源になる()
    {
        using var factory = new Factory(new Dictionary<string, string?>
        {
            ["Fx:Provider"] = "fred",
            ["Fx:Fred:ApiKey"] = "key",
        });
        _ = factory.CreateClient();

        factory.Services.GetRequiredService<IFxRateSource>().Should().BeOfType<CachingFxRateSource>();
    }

    [Fact]
    public void レート源はsingletonでキャッシュが共有される()
    {
        // 判断ごとに作り直すと TTL キャッシュが効かず、レート源へ毎回問い合わせてしまう。
        using var factory = new Factory(new Dictionary<string, string?>
        {
            ["Fx:Provider"] = "fred",
            ["Fx:Fred:ApiKey"] = "key",
        });
        _ = factory.CreateClient();

        var first = factory.Services.GetRequiredService<IFxRateSource>();
        var second = factory.Services.GetRequiredService<IFxRateSource>();

        first.Should().BeSameAs(second);
    }

    [Fact]
    public void 判断側ポートは市場から通貨を導くアダプタで解決される()
    {
        using var factory = new Factory();
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IFxRateProvider>().Should().BeOfType<MarketFxRateProvider>();
    }

    [Fact]
    public async Task 実効構成の自己申告に選択中のレート源を載せる()
    {
        using var factory = new Factory(new Dictionary<string, string?>
        {
            ["Fx:Provider"] = "fred",
            ["Fx:Fred:ApiKey"] = "key",
        });

        var dto = await factory.CreateClient()
            .GetFromJsonAsync<ServiceIntrospectionDto>(ShimIntrospection.IntrospectionPath);

        dto!.Ports.Should().ContainSingle(p => p.Port == "fx-rate" && p.Implementation == "fred");
    }

    [Fact]
    public async Task APIキーが無ければ自己申告もnoneを示す()
    {
        // 自己申告と実際の選択がずれると、構成不備の検知そのものが嘘になる（規則は ResolveProvider が単一情報源）。
        using var factory = new Factory(new Dictionary<string, string?> { ["Fx:Provider"] = "fred" });

        var dto = await factory.CreateClient()
            .GetFromJsonAsync<ServiceIntrospectionDto>(ShimIntrospection.IntrospectionPath);

        dto!.Ports.Should().ContainSingle(p => p.Port == "fx-rate" && p.Implementation == "none");
    }

    private sealed class Factory(IDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // 実効構成の自己申告は Program.cs が登録時に構成を読むため、UseSetting（ホスト構成）で与える。
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");
            foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
                builder.UseSetting(key, value);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBusControl>();
                services.AddMassTransitTestHarness(x => x.AddConsumer<Composable.Steps.PriceMovementDetectedConsumer>());
            });
        }
    }
}
