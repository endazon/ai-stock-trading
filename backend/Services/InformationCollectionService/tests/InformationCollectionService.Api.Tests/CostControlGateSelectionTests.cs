using InformationCollectionService.Application.Ports;
using InformationCollectionService.Infrastructure.Adapters;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace InformationCollectionService.Api.Tests;

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

    [Fact]
    public void 不正なBaseUrlは安全既定のプレースホルダ()
    {
        // Uri.TryCreate（絶対 URI）が失敗する文字列は安全既定へ倒す（IADR-0031）。
        using var factory = new Factory(costBaseUrl: "not-a-url");
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICostControlGate>().Should().BeOfType<PlaceholderCostControlGate>();
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
                // ADR-0013, IADR-0129, #354: 実 RabbitMQ へ接続しない（Wolverine の外部トランスポートを無効化する）。
                services.DisableAllExternalWolverineTransports();
            });
        }
    }
}
