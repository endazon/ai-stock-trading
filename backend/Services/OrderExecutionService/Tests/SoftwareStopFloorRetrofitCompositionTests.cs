using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Wolverine;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-1933, FR-10, ADR-0049, #1136, IADR-0472 決定1: **本番の Program.cs の組み立て**（moomoo 構成）で解決したガードが、
// 巡回の先頭で S1 の損切りラインへ下限を遡及する。遡及の口はガードの省略可能な引数なので、Program.cs が渡し忘れても
// コンパイルも単体の試験も通ったまま、遡及が一度も走らない。殺す変異: Program.cs から遡及の口を外す（ラインが 226.52 のまま・赤）。
public class SoftwareStopFloorRetrofitCompositionTests
{
    private static readonly DateTimeOffset ArmedAt = DateTimeOffset.UtcNow.AddDays(-1);

    private sealed class ProgramFactory(StopLegScriptedBroker broker) : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Broker:Provider", "moomoo");
            builder.UseSetting("Broker:Environment", "sim");
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBrokerAdapter>();
                services.AddSingleton<IBrokerAdapter>(broker);

                var toRemove = services
                    .Where(d => d.ServiceType == typeof(DbContextOptions<OrderExecutionDbContext>)
                             || (d.ServiceType.IsGenericType
                                 && d.ServiceType.GetGenericTypeDefinition().FullName?
                                     .Contains("IDbContextOptionsConfiguration") == true
                                 && d.ServiceType.GenericTypeArguments.Length == 1
                                 && d.ServiceType.GenericTypeArguments[0] == typeof(OrderExecutionDbContext)))
                    .ToList();
                foreach (var d in toRemove) services.Remove(d);
                services.AddDbContext<OrderExecutionDbContext>(opt => opt.UseInMemoryDatabase(_dbName));

                var ownHosted = services
                    .Where(d => d.ServiceType == typeof(IHostedService)
                             && d.ImplementationType?.Assembly == typeof(Program).Assembly)
                    .ToList();
                foreach (var d in ownHosted) services.Remove(d);

                services.DisableAllExternalWolverineTransports();
            });
        }
    }

    [Fact]
    public async Task T_10_1933_Programのガードは巡回の先頭でS1の損切りラインへ下限を遡及する()
    {
        var broker = new StopLegScriptedBroker
        {
            Positions = [new BrokerPositionSnapshot("NVDA", Market.UnitedStates, 10, 230.82m)],
        };
        await using var factory = new ProgramFactory(broker);

        var entry = Guid.NewGuid();
        using (var seed = factory.Services.CreateScope())
        {
            seed.ServiceProvider.GetRequiredService<IExecutedOrderStore>().Save(new ExecutionRecord(
                entry, "entry-nvda", "NVDA", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, PositionEffect.Open,
                10, 230.82m, 10, 230.82m, OrderStatus.Filled, 0m, ArmedAt));
            seed.ServiceProvider.GetRequiredService<IProtectiveStopOrderStore>().Save(new ProtectiveStopOrder(
                entry, ProtectiveStopIds.SoftwareStopId(entry), string.Empty, "NVDA", Market.UnitedStates, TradeSide.Buy,
                ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 226.52m, 1m, 0, ProtectiveStopState.Active,
                ArmedAt, ArmedAt, StopLossExecutionMethod.SoftwareStop, RemainingProtected: 10));
        }

        ProtectiveStopGuardResult result;
        using (var scope = factory.Services.CreateScope())
            result = await scope.ServiceProvider.GetRequiredService<ProtectiveStopGuard>().RunOnceAsync(10);

        result.Events.OfType<SoftwareStopLineWidened>().Should().ContainSingle()
            .Which.StopLossPrice.Should().Be(226.2036m);
        using var check = factory.Services.CreateScope();
        check.ServiceProvider.GetRequiredService<IProtectiveStopOrderStore>().Find(entry)!.TriggerPrice
            .Should().Be(226.2036m, "本番の EF ストアへ保存される");
    }
}
