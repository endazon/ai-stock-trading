using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;
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

// 🔴 T-10-3001, FR-10, NFR, #1444（親 #1423 M-1）, IADR-0574 決定1: **本番の Program.cs の組み立て**（moomoo 構成）で解決した
// ガードと S1 の実行器が、閉場中は新しい注文を送らない。市場時間の口はどちらも省略可能な引数なので、Program.cs が渡し忘れても
// コンパイルも単体の試験も通ったまま閉場中に再発注・成行を送る。殺す変異: Program.cs からガード・実行器のどちらかの
// `marketSession:` を外す（閉場中に逆指値・成行が出る・赤）。
public class MarketSessionGateCompositionTests
{
    // 2026-10-22（木）20:30 EDT（閉場）と 2026-10-23（金）09:30 EDT（寄り付き）。
    private static readonly DateTimeOffset Closed = new(2026, 10, 23, 0, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Open = new(2026, 10, 23, 13, 30, 0, TimeSpan.Zero);

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = Closed;
    }

    private sealed class ProgramFactory(StopLegScriptedBroker broker, MutableClock clock) : WebApplicationFactory<Program>
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
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(clock);

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
                services.DisableAllWolverineMessagePersistence();
            });
        }
    }

    [Fact]
    public async Task T_10_3001_Programのガードと実行器は閉場中に逆指値も成行も送らず開場で張り直す()
    {
        var broker = new StopLegScriptedBroker();
        var clock = new MutableClock();
        await using var factory = new ProgramFactory(broker, clock);

        factory.Services.GetRequiredService<IMarketSessionGate>().Should().BeOfType<MarketHoursSessionGate>(
            "本番は共有カーネルの MarketHours で判定する");

        var s0 = Guid.NewGuid();
        var s1 = Guid.NewGuid();
        var armedAt = Closed.AddHours(-10);
        using (var seed = factory.Services.CreateScope())
        {
            var stops = seed.ServiceProvider.GetRequiredService<IProtectiveStopOrderStore>();
            // S0: 引けで失効した当日限りの逆指値（照会で Cancelled）。
            stops.Save(new ProtectiveStopOrder(
                s0, Guid.NewGuid(), StopLegScriptedBroker.LapsedStopOrderId, "AAPL", Market.UnitedStates, TradeSide.Buy,
                ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, Attempt: 1, ProtectiveStopState.Active,
                armedAt, armedAt));
        }

        // 1) 閉場中のガード: 張り直さない・成行も送らない。
        ProtectiveStopGuardResult closed;
        using (var scope = factory.Services.CreateScope())
            closed = await scope.ServiceProvider.GetRequiredService<ProtectiveStopGuard>().RunOnceAsync(10);
        closed.AwaitingMarketOpen.Should().Be(1);
        broker.StopPlaceCount.Should().Be(0, "閉場中は逆指値を張り直さない（ガードへ市場時間の口が渡っている）");
        broker.MarketCloseCount.Should().Be(0);

        // 2) 閉場後に届いた S1 の到達: 実行器は成行を送らない（実行器へ市場時間の口が渡っている）。
        using (var seed = factory.Services.CreateScope())
        {
            seed.ServiceProvider.GetRequiredService<IExecutedOrderStore>().Save(new ExecutionRecord(
                s1, "entry-s1", "MSFT", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, PositionEffect.Open,
                5, 400m, 5, 400m, OrderStatus.Filled, 0m, armedAt));
            seed.ServiceProvider.GetRequiredService<IProtectiveStopOrderStore>().Save(new ProtectiveStopOrder(
                s1, ProtectiveStopIds.SoftwareStopId(s1), string.Empty, "MSFT", Market.UnitedStates, TradeSide.Buy,
                ProductType.Cash, BrokerProvider.MoomooSimulate, 5, 390m, 1m, 0, ProtectiveStopState.Active,
                armedAt, armedAt, StopLossExecutionMethod.SoftwareStop, RemainingProtected: 5));
        }

        broker.Positions =
        [
            new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 10, 1_000m),
            new BrokerPositionSnapshot("MSFT", Market.UnitedStates, 5, 400m),
        ];
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<SoftwareStopExecutor>().OnTriggeredAsync(new StopLossTriggered(
                Guid.NewGuid(), "MSFT", Market.UnitedStates, TradeSide.Buy, 5, 385m, 390m, Closed.AddMinutes(-1)));
        }

        broker.MarketCloseCount.Should().Be(0, "🔴 閉場中は S1 の成行を送らない");

        // 3) 寄り付き: ガードが逆指値を張り直し、到達済みの S1 を成行で決済する。
        clock.UtcNow = Open;
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ProtectiveStopGuard>().RunOnceAsync(10);
        broker.StopPlaceCount.Should().Be(1, "開場後の最初の巡回で張り直す");
        broker.MarketCloseCount.Should().Be(1, "到達済みの S1 は開場の巡回で決済する");
    }
}
