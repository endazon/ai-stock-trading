using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.PollOrderFills;
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
using Behavior = OrderExecutionService.Tests.StopLegScriptedBroker.StopBehavior;

namespace OrderExecutionService.Tests;

// 🔴 T-10-2981, FR-10, UC-02, #1422, IADR-0569 決定4: **本番の Program.cs の組み立て**で、約定追跡（OrderFillPoller）が発注執行の後始末の口を持ち、
// 「取消の確認待ち」だった新規建てが取消の窓で約定して終端したら、約定数量を成行で手仕舞うことを固定する。
// 口は約定追跡の省略可能な引数である。Program.cs が渡し忘れても、コンパイルも単体の試験（自分で new する）も通ったまま、
// 取消の窓で約定した建玉は保護も手仕舞いも無く残る。外界（ブローカー・DB・常駐・外部トランスポート・時計）だけを差し替える。
// 殺す変異: Program.cs の OrderFillPoller へ渡す口を落とす（成行 0 本・後始末 0 件で赤）。
public class UnconfirmedEntryCancelCompositionTests
{
    private static readonly DateTimeOffset Now = StopLegScriptedBroker.Now;

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

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
                // 時計は固定する（約定追跡の追跡上限の窓を実時刻に依存させない）。
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new FixedClock());

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
    public async Task Programの約定追跡は取消の確認待ちだったエントリーの約定を成行で手仕舞う()
    {
        var broker = new StopLegScriptedBroker { Stop = Behavior.Reject };
        await using var factory = new ProgramFactory(broker);
        var entry = Guid.NewGuid();
        using (var seed = factory.Services.CreateScope())
        {
            seed.ServiceProvider.GetRequiredService<IExecutedOrderStore>().Save(new ExecutionRecord(
                entry, "entry-7", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, PositionEffect.Open,
                10, 1_000m, FilledQuantity: 0, AveragePrice: 0m, OrderStatus.Accepted, SlippageRatio: 0m, ExecutedAt: Now.AddMinutes(-1)));
            seed.ServiceProvider.GetRequiredService<IProtectiveStopOrderStore>().Save(new ProtectiveStopOrder(
                entry, ProtectiveStopIds.StopDecisionId(entry, 1), string.Empty, "AAPL", Market.UnitedStates, TradeSide.Buy,
                ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, 0, ProtectiveStopState.AwaitingEntryCancel,
                Now.AddMinutes(-1), Now.AddMinutes(-1)));
        }

        broker.Orders["entry-7"] = new BrokerOrder(
            "entry-7", StopLegScriptedBroker.CloseIntent(10), OrderStatus.Filled, 10, 1_001m, Now, Now);

        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<OrderFillPoller>()
            .PollOnceAsync(TimeSpan.FromHours(24), batchSize: 100);

        result.EntryCancelResolutions.Should().ContainSingle()
            .Which.Remediation.Should().Be(ProtectiveStopRemediation.PositionClosed);
        broker.MarketCloseCount.Should().Be(1, "Program.cs の約定追跡が後始末の口を持っていれば、約定数量を成行で手仕舞う");
        using var check = factory.Services.CreateScope();
        check.ServiceProvider.GetRequiredService<IProtectiveStopOrderStore>().Find(entry)!.State
            .Should().Be(ProtectiveStopState.Completed);
    }
}
