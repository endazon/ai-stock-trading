using System.Reflection;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Observability;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;
using OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Infrastructure.Persistence;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-1774, NFR, FR-10, #1092, IADR-0462 決定3: **本番の Program.cs の組み立て**が、建玉照会の状態の報告口を
// 発行の実装（singleton 1 つ）で登録し、ガード・S1・発注へ**同じ実体**を渡すことを固定する。
//
// 業務クラスの引数は省略可能（既定 NoOp）なので、Program.cs が渡し忘れてもコンパイルも単体の試験も通り、
// 台帳には何も出ないまま夜が過ぎる（#1092 の症状の再発）。別々の実体を渡すと、発生源の状態が分かれて同じ遷移を二重に出す。
// 常駐（観測・稼働 probe）は DI が解決する省略可能引数であり、CompositionWiringGuardTests（W1）が解決漏れを検査する。
public class PositionQueryHealthCompositionTests
{
    private sealed class MoomooFactory : WebApplicationFactory<Program>
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
                services.AddSingleton<IBrokerAdapter>(new NullPositionBroker());

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

                // 自前の常駐は止める（偽のブローカーを巡回で叩かせない）。
                var ownHosted = services
                    .Where(d => d.ServiceType == typeof(IHostedService)
                             && d.ImplementationType?.Assembly == typeof(Program).Assembly)
                    .ToList();
                foreach (var d in ownHosted) services.Remove(d);

                services.DisableAllExternalWolverineTransports();
            });
        }
    }

    // 建玉照会は常に不明（null）を返す moomoo 相当の偽物。
    private sealed class NullPositionBroker : IBrokerAdapter, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BrokerPositionSnapshot>?>(null);

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException("本試験は発注しない");

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static object? Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    [Fact]
    public async Task T_10_1774_Programは発行の実装を1つだけ登録し_ガードとS1と発注へ同じ実体を渡す()
    {
        await using var factory = new MoomooFactory();
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetServices<IPositionQueryHealthReporter>().Should().ContainSingle();
        var singleton = sp.GetRequiredService<IPositionQueryHealthReporter>();
        singleton.Should().BeOfType<PositionQueryHealthReporter>();

        Field(sp.GetRequiredService<ProtectiveStopGuard>(), "_positionQueryHealth").Should().BeSameAs(singleton);
        Field(sp.GetRequiredService<SoftwareStopExecutor>(), "_positionQueryHealth").Should().BeSameAs(singleton);
        Field(sp.GetRequiredService<OrderExecutionAppService>(), "_positionQueryHealth").Should().BeSameAs(singleton);
    }

    // T-10-1774: 発行はランタイムの MessageBus を通り、PositionQueryStatusChanged が外へ出る（起動直後の失敗）。
    [Fact]
    public async Task T_10_1774_Programの報告口は起動直後の失敗をPositionQueryStatusChangedとして発行する()
    {
        await using var factory = new MoomooFactory();
        var reporter = factory.Services.GetRequiredService<IPositionQueryHealthReporter>();
        var host = factory.Services.GetRequiredService<IHost>();

        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(
            (Func<IMessageContext, Task>)(_ => reporter.ReportAsync(PositionQuerySource.ProtectiveStopGuard, false, "Other")));

        var e = session.Sent.SingleMessage<PositionQueryStatusChanged>();
        e.Source.Should().Be(PositionQuerySource.ProtectiveStopGuard);
        e.Status.Should().Be(PositionQueryStatus.Failing);
        e.PreviousStatus.Should().Be(PositionQueryStatus.Unknown);
        e.FailureKind.Should().Be("Other");
    }
}
