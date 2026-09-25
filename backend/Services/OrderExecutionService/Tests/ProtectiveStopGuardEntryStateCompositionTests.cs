using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Infrastructure.Persistence;
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

// 🔴 T-10-1135, FR-10, UC-02, #1013, IADR-0428（2026-09-26 追記）: **本番の Program.cs の組み立て**で解決したガードが、
// エントリー注文の状態（発注記録＋ブローカーの注文照会）を見てから「建玉 0」を建玉消滅と読むことを固定する。
//
// 依存は増やしていない（発注記録のストア・ブローカー・建玉照会はもとからガードの引数）。それでも組み立てで固定するのは、
// ガードが本番で受け取る発注記録のストア（EF）と注文照会の口が、単体の試験で自分で new したものと同じ振る舞いをすることを
// 確かめるためである（発注記録が本番のストアから引けなければ、全行が「不明」で据え置かれ、建玉なき逆指値が残る）。
//
// 殺す変異: ガードがエントリーの状態を見ずに取り消す（1 巡目で取消 1 本・赤）／本番のストアから記録が引けない（2 巡目で取消 0 本・赤）。
public class ProtectiveStopGuardEntryStateCompositionTests
{
    private static readonly DateTimeOffset ArmedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

    // Program.cs を moomoo 構成で組む。外界だけを差し替える（ReconciledEntryProtectionCompositionTests と同じ作法）。
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

    private static BrokerOrder Order(string orderId, OrderStatus status, int filled, PositionEffect effect) =>
        new(orderId, new OrderIntent("AAPL", Market.UnitedStates, effect == PositionEffect.Open ? TradeSide.Buy : TradeSide.Sell,
                ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 1_000m, effect),
            status, filled, filled > 0 ? 1_000m : 0m, ArmedAt, OrderStatusLifecycle.IsTerminal(status) ? ArmedAt : null);

    [Fact]
    public async Task Programのガードは未約定のエントリーで建玉が0でも逆指値を取り消さず_約定しないまま終われば取り消す()
    {
        var broker = new StopLegScriptedBroker { Positions = [] }; // 指値のエントリーがまだ約定していない
        broker.Orders["stop-live"] = Order("stop-live", OrderStatus.Accepted, 0, PositionEffect.Close);
        broker.Orders["entry-live"] = Order("entry-live", OrderStatus.Accepted, 0, PositionEffect.Open);
        await using var factory = new ProgramFactory(broker);

        var entry = Guid.NewGuid();
        using (var seed = factory.Services.CreateScope())
        {
            seed.ServiceProvider.GetRequiredService<IExecutedOrderStore>().Save(new ExecutionRecord(
                entry, "entry-live", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, PositionEffect.Open,
                10, 1_000m, 0, 0m, OrderStatus.Accepted, 0m, ArmedAt));
            seed.ServiceProvider.GetRequiredService<IProtectiveStopOrderStore>().Save(new ProtectiveStopOrder(
                entry, ProtectiveStopIds.StopDecisionId(entry, 1), "stop-live", "AAPL", Market.UnitedStates, TradeSide.Buy,
                ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, 1, ProtectiveStopState.Active, ArmedAt, ArmedAt));
        }

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ProtectiveStopGuard>().RunOnceAsync(10);

        broker.CancelCount.Should().Be(0, "本番の組み立てでも、未約定のエントリーの建玉 0 を建玉消滅と読まない");
        using (var check = factory.Services.CreateScope())
        {
            check.ServiceProvider.GetRequiredService<IProtectiveStopOrderStore>().Find(entry)!.State
                .Should().Be(ProtectiveStopState.Active);
        }

        // エントリーが約定しないまま失効した → 建玉は生じなかった。残る逆指値は取り消す（建玉なき逆指値を残さない）。
        broker.Orders["entry-live"] = Order("entry-live", OrderStatus.Expired, 0, PositionEffect.Open);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ProtectiveStopGuard>().RunOnceAsync(10);

        broker.CancelCount.Should().Be(1);
        using var after = factory.Services.CreateScope();
        after.ServiceProvider.GetRequiredService<IProtectiveStopOrderStore>().Find(entry)!.State
            .Should().Be(ProtectiveStopState.Completed);
    }
}
