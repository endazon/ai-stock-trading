using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace InformationCollectionService.Tests;

// FR-08, IADR-0069 決定 4: KnowledgeBase:Documents:BaseUrl の有無で IKnowledgeBaseSink が安全既定
// （Logging＝no-op）／実 KB 保存（KnowledgeBaseWriterSink）に切り替わることを検証する。既定は現行挙動を保持する。
public class KnowledgeBaseSinkSelectionTests
{
    [Fact]
    public void BaseUrl未設定は安全既定のLoggingシンク()
    {
        using var factory = new Factory(documentsBaseUrl: null);
        _ = factory.CreateClient();

        factory.Services.GetRequiredService<IKnowledgeBaseSink>().Should().BeOfType<LoggingKnowledgeBaseSink>();
    }

    [Fact]
    public void DocumentsBaseUrl設定時は実KB保存シンク()
    {
        using var factory = new Factory(documentsBaseUrl: "http://documents");
        _ = factory.CreateClient();

        factory.Services.GetRequiredService<IKnowledgeBaseSink>().Should().BeOfType<KnowledgeBaseWriterSink>();
    }

    [Fact]
    public void 不正なBaseUrlは安全既定のLoggingシンク()
    {
        using var factory = new Factory(documentsBaseUrl: "not-a-url");
        _ = factory.CreateClient();

        factory.Services.GetRequiredService<IKnowledgeBaseSink>().Should().BeOfType<LoggingKnowledgeBaseSink>();
    }

    private sealed class Factory(string? documentsBaseUrl) : WebApplicationFactory<Program>
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
                if (documentsBaseUrl is not null)
                    settings["KnowledgeBase:Documents:BaseUrl"] = documentsBaseUrl;
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
