using AiStockTrading.Audit.Worker.Composable.Steps;
using AiStockTrading.Audit.Worker.Foundation.Persistence;
using MassTransit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiStockTrading.Audit.Worker.Tests;

// WebApplicationFactory（他 Worker テスト準拠）。実 RabbitMQ/Postgres/Keycloak に依存せず、InMemory DB・
// MassTransit テストハーネス・TestAuthHandler へ差し替えてイベント記録と照会エンドポイントを検証する。
public sealed class AuditWorkerWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:ConnectionString"] = "amqp://localhost",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test",
            }));

        builder.ConfigureServices(services =>
        {
            ReplaceDbContextWithInMemory(services, _dbName);

            // 実 RabbitMQ 接続を避けるため MassTransit をテストハーネスへ差し替える（全 6 コンシューマを登録）。
            services.RemoveAll<IBusControl>();
            services.AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<PriceMovementDetectedAuditConsumer>();
                x.AddConsumer<StopLossTriggeredAuditConsumer>();
                x.AddConsumer<TradeDecisionMadeAuditConsumer>();
                x.AddConsumer<OrderApprovedAuditConsumer>();
                x.AddConsumer<OrderRejectedAuditConsumer>();
                x.AddConsumer<OrderExecutedAuditConsumer>();
            });

            // Keycloak/JWT に依存せず TestAuthHandler で認証する（既定スキームを Test に切替）。
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    private static void ReplaceDbContextWithInMemory(IServiceCollection services, string dbName)
    {
        var toRemove = services
            .Where(d => d.ServiceType == typeof(DbContextOptions<AuditDbContext>)
                     || (d.ServiceType.IsGenericType
                         && d.ServiceType.GetGenericTypeDefinition().FullName?
                             .Contains("IDbContextOptionsConfiguration") == true
                         && d.ServiceType.GenericTypeArguments.Length == 1
                         && d.ServiceType.GenericTypeArguments[0] == typeof(AuditDbContext)))
            .ToList();
        foreach (var d in toRemove) services.Remove(d);

        services.AddDbContext<AuditDbContext>(opt => opt.UseInMemoryDatabase(dbName));
    }
}
