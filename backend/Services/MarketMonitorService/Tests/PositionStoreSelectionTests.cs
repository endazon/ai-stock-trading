using MarketMonitorService.Features.MarketMonitor;
using MarketMonitorService.Infrastructure.ExternalServices;
using MarketMonitorService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
// IADR-0128: consumer は Infrastructure へ移った。相対名（Composable.Steps.*）参照をテスト本文を触らずに解決する。
using Composable = MarketMonitorService.Infrastructure;

namespace MarketMonitorService.Tests;

// FR-03, FR-10, IADR-0030: RiskManagement:BaseUrl の有無で IPositionStore が安全既定（プレースホルダ・空）/
// 同期照会（Http）に切り替わることを検証する。選択は解決時に構成を読む（WebApplicationFactory の構成上書きに追随する）。
public class PositionStoreSelectionTests
{
    [Fact]
    public void BaseUrl未設定は安全既定のプレースホルダ()
    {
        using var factory = new Factory(riskBaseUrl: null);
        _ = factory.CreateClient(); // ホスト起動

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IPositionStore>().Should().BeOfType<PlaceholderPositionStore>();
    }

    [Fact]
    public void BaseUrl設定時はリスク管理を同期照会する_Http実装()
    {
        using var factory = new Factory(riskBaseUrl: "http://risk");
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IPositionStore>().Should().BeOfType<HttpPositionStore>();
    }

    private sealed class Factory(string? riskBaseUrl) : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["RabbitMq:ConnectionString"] = "amqp://localhost",
                    ["Otlp:Endpoint"] = "http://localhost:4317",
                    ["Auth:Authority"] = "https://localhost/realms/test",
                };
                if (riskBaseUrl is not null)
                    settings["RiskManagement:BaseUrl"] = riskBaseUrl;
                cfg.AddInMemoryCollection(settings);
            });

            builder.ConfigureServices(services =>
            {
                var toRemove = services
                    .Where(d => d.ServiceType == typeof(DbContextOptions<MarketMonitorDbContext>)
                             || (d.ServiceType.IsGenericType
                                 && d.ServiceType.GetGenericTypeDefinition().FullName?
                                     .Contains("IDbContextOptionsConfiguration") == true
                                 && d.ServiceType.GenericTypeArguments.Length == 1
                                 && d.ServiceType.GenericTypeArguments[0] == typeof(MarketMonitorDbContext)))
                    .ToList();
                foreach (var d in toRemove) services.Remove(d);
                services.AddDbContext<MarketMonitorDbContext>(opt => opt.UseInMemoryDatabase(_dbName));

                // ADR-0013, IADR-0129, #354: 実 RabbitMQ へ接続しない（ハンドラの発見は Program.cs 側の配線が担う）。
                services.DisableAllExternalWolverineTransports();

                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            });
        }
    }
}
