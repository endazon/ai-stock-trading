using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.InformationCollection.Worker.Composable.Adapters;
using FluentAssertions;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AiStockTrading.InformationCollection.Worker.Tests;

// NFR（費用）, IADR-0031: CostControl:BaseUrl の有無で ICostControlGate が安全既定（プレースホルダ・Normal）/
// 同期照会（Http）に切り替わることを検証する。選択は解決時に構成を読む（WebApplicationFactory の構成上書きに追随する）。
public class CostControlGateSelectionTests
{
    [Fact]
    public void BaseUrl未設定は安全既定のプレースホルダ()
    {
        using var factory = new Factory(costBaseUrl: null);
        _ = factory.CreateClient(); // ホスト起動

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICostControlGate>().Should().BeOfType<PlaceholderCostControlGate>();
    }

    [Fact]
    public void BaseUrl設定時は費用統制を同期照会する_Http実装()
    {
        using var factory = new Factory(costBaseUrl: "http://cost");
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICostControlGate>().Should().BeOfType<HttpCostControlGate>();
    }

    private sealed class Factory(string? costBaseUrl) : WebApplicationFactory<Program>
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
                    ["Collection:PollIntervalSeconds"] = "3600",
                };
                if (costBaseUrl is not null)
                    settings["CostControl:BaseUrl"] = costBaseUrl;
                cfg.AddInMemoryCollection(settings);
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBusControl>();
                services.AddMassTransitTestHarness();
            });
        }
    }
}
