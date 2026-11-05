using System.Net;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using AwesomeAssertions;
using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace InformationCollectionService.Tests;

// FR-01, FR-08, ADR-0006, #1525, IADR-0608 決定1: 単一ノードのクラウドの構成（values-cloud）は KB の宛先を持たない。
// 宛先を空にしたまま起動し、保存は no-op（収集そのものは止めない）になることを固定する。
public class CloudProfileWithoutKnowledgeBaseTests
{
    [Fact]
    public async Task KBの宛先が空でも起動し保存はno_opになる()
    {
        await using var factory = new Factory();

        (await factory.CreateClient().GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Services.GetRequiredService<IKnowledgeBaseSink>().Should().BeOfType<LoggingKnowledgeBaseSink>();
        factory.Services.GetRequiredService<IKnowledgeBaseWriter>().GetType().Name.Should().Be("NoOpKnowledgeBaseWriter");
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RabbitMq:ConnectionString"] = "amqp://localhost",
                    ["Otlp:Endpoint"] = "http://localhost:4317",
                    ["Collection:PollIntervalSeconds"] = "3600",
                    ["KnowledgeBase:Documents:BaseUrl"] = "",
                    ["KnowledgeBase:Search:BaseUrl"] = "",
                }));
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
