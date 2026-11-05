using System.Reflection;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// 🔴 FR-04, FR-10, #1529, IADR-0617: 本番の Program.cs が組んだ判断サービスは、構成 TradeDecision:AllowUnanchoredEntryPrice に従う。
// 既定（未設定）と false は見送り、true でだけ参照価格での新規建てを許す（ホストを組んで、判断サービスへ渡った値を見る）。
public class UnanchoredEntryPriceWiringTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void 本番の組み立ての判断サービスは構成の値に従う(string? configured, bool expected)
    {
        using var factory = new Factory(configured);
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<UnanchoredEntryPriceOptions>().Allow.Should().Be(expected);
        AllowOf(scope.ServiceProvider.GetRequiredService<TradeDecisionAppService>()).Should().Be(expected);
    }

    [Fact]
    public void 構成のキーは_TradeDecision_AllowUnanchoredEntryPrice() =>
        UnanchoredEntryPriceOptions.ConfigKey.Should().Be("TradeDecision:AllowUnanchoredEntryPrice");

    private static bool AllowOf(TradeDecisionAppService service) =>
        (bool)typeof(TradeDecisionAppService)
            .GetField("_allowUnanchoredEntryPrice", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;

    private sealed class Factory(string? configured) : WebApplicationFactory<Program>
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
                cfg.AddInMemoryCollection(settings);
            });
            // Program.cs は組み立ての最中（builder.Build の前）に構成を読むため、ConfigureAppConfiguration では届かない。
            // 本番の環境変数と同じく、最初から在る構成として与える。
            if (configured is not null)
                builder.UseSetting(UnanchoredEntryPriceOptions.ConfigKey, configured);
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
