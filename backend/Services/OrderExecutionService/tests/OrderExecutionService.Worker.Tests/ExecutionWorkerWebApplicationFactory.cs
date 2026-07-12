using AiStockTrading.OrderExecution.Worker.Foundation.Persistence;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiStockTrading.OrderExecution.Worker.Tests;

// WebApplicationFactory（他 Worker テスト準拠）。実 RabbitMQ/Postgres に依存せず、InMemory DB・
// MassTransit テストハーネスへ差し替える。ブローカは既定（paper）で安全。
public sealed class ExecutionWorkerWebApplicationFactory : WebApplicationFactory<Program>
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
                ["Broker:Provider"] = "paper",
            }));

        builder.ConfigureServices(services =>
        {
            ReplaceDbContextWithInMemory(services, _dbName);

            services.RemoveAll<IBusControl>();
            services.AddMassTransitTestHarness(x => x.AddConsumer<Composable.Steps.OrderApprovedConsumer>());
        });
    }

    private static void ReplaceDbContextWithInMemory(IServiceCollection services, string dbName)
    {
        var toRemove = services
            .Where(d => d.ServiceType == typeof(DbContextOptions<OrderExecutionDbContext>)
                     || (d.ServiceType.IsGenericType
                         && d.ServiceType.GetGenericTypeDefinition().FullName?
                             .Contains("IDbContextOptionsConfiguration") == true
                         && d.ServiceType.GenericTypeArguments.Length == 1
                         && d.ServiceType.GenericTypeArguments[0] == typeof(OrderExecutionDbContext)))
            .ToList();
        foreach (var d in toRemove) services.Remove(d);

        services.AddDbContext<OrderExecutionDbContext>(opt => opt.UseInMemoryDatabase(dbName));
    }
}
