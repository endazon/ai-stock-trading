using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
using TradeDecisionService.Infrastructure.ExternalServices;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// T-10-1631, FR-04, FR-15, ADR-0044 決定 3, ADR-0046 決定 1, #1049, IADR-0442 決定 3・4: 本番の組み立てで、当時の監視銘柄の供給は
// MarketMonitor:BaseUrl があれば HTTP（市場監視の as-of の口）、なければ未結線（常に再構成できない）になり、as-of 入力の供給は
// そのデコレータ越しになる（記録の対象銘柄で代える経路は無い）。
public class AsOfWatchlistSourceSelectionTests
{
    [Fact]
    public async Task BaseUrl未設定なら未結線で供給はデコレータ越し()
    {
        using var factory = new Factory(monitorBaseUrl: null);
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<IAsOfWatchlistSource>();
        source.Should().BeOfType<UnwiredAsOfWatchlistSource>();
        (await source.GetWatchlistAtAsync(DateTimeOffset.UtcNow)).Symbols.Should().BeNull();
        scope.ServiceProvider.GetRequiredService<IAsOfDecisionInputProvider>()
            .Should().BeOfType<WatchlistAsOfDecisionInputProvider>();
    }

    [Fact]
    public void BaseUrl設定時はHTTPで市場監視を照会する()
    {
        using var factory = new Factory(monitorBaseUrl: "http://monitor");
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IAsOfWatchlistSource>().Should().BeOfType<HttpAsOfWatchlistSource>();
        scope.ServiceProvider.GetRequiredService<IAsOfDecisionInputProvider>()
            .Should().BeOfType<WatchlistAsOfDecisionInputProvider>();
    }

    private sealed class Factory(string? monitorBaseUrl) : WebApplicationFactory<Program>
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
                if (monitorBaseUrl is not null)
                    settings["MarketMonitor:BaseUrl"] = monitorBaseUrl;
                cfg.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
