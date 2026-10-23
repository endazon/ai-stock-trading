using System.Reflection;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;
using OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Ports;
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

// 🔴 T-10-3008, NFR, FR-10, #1446（親 #1423 M-2）, IADR-0575 決定 2: **本番の Program.cs の組み立て**（moomoo 構成）で、
// 決済を送る 3 つの組み立て（承認の受け口・S1 の実行器・保護逆指値ガード）が**同じ**決済の直列化を受け取る。
// 直列化はどれも省略可能な引数なので、Program.cs が渡し忘れるとコンパイルも単体の試験も通ったまま、その経路だけが
// プロセス内の既定へ倒れ、入れ替え中の新旧の Pod の間で直列にならない。殺す変異: Program.cs から 3 つのどれかの直列化の引数を外す（赤）。
// あわせて、直列化の選び方（関係 DB なら advisory lock・試験の InMemory ならプロセス内）を固定する。
public class ClosePositionLockCompositionTests
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 23, 14, 0, 0, TimeSpan.Zero);
    }

    private sealed class RecordingLock : IClosePositionLock
    {
        public Task<IAsyncDisposable> AcquireAsync(ClosePositionKey key, CancellationToken cancellationToken) =>
            throw new NotSupportedException("組み立ての試験では取らない");
    }

    private sealed class InMemoryProgramFactory() : ProgramFactoryBase(null);

    private sealed class ProgramFactory(IClosePositionLock closeLock) : ProgramFactoryBase(closeLock);

    private class ProgramFactoryBase(IClosePositionLock? closeLock) : WebApplicationFactory<Program>
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
                services.AddSingleton<IBrokerAdapter>(new StopLegScriptedBroker());
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

                // 組み立ての検証だけを行う（常駐は止める）。
                var ownHosted = services
                    .Where(d => d.ServiceType == typeof(IHostedService)
                             && d.ImplementationType?.Assembly == typeof(Program).Assembly)
                    .ToList();
                foreach (var d in ownHosted) services.Remove(d);

                // 直列化だけを差し替え、3 つの組み立てが同じものを受け取るかを見る。
                if (closeLock is not null)
                {
                    services.RemoveAll<IClosePositionLock>();
                    services.AddSingleton(closeLock);
                }

                services.DisableAllExternalWolverineTransports();
                services.DisableAllWolverineMessagePersistence();
            });
        }
    }

    private static IClosePositionLock LockOf(object service) =>
        (IClosePositionLock)service.GetType()
            .GetField("_closeLock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;

    // 🔴 InMemory の DbContext のホストでも、3 つの組み立てが同じホストの 1 つのセマフォを受け取り、別のホストとは共有しない
    // （PR #1447 の CI: static の共有だと並列の試験のホストどうしが待ち合い、Wolverine の追跡の上限で落ちた）。
    [Fact]
    public async Task T_10_3008_InMemoryのホストでも3つは同じセマフォを受け取り別のホストとは共有しない()
    {
        await using var first = new InMemoryProgramFactory();
        await using var second = new InMemoryProgramFactory();
        using var a = first.Services.CreateScope();
        using var b = second.Services.CreateScope();

        var lockA = LockOf(a.ServiceProvider.GetRequiredService<OrderExecutionAppService>());
        lockA.Should().BeOfType<InProcessClosePositionLock>();
        LockOf(a.ServiceProvider.GetRequiredService<SoftwareStopExecutor>()).Should().BeSameAs(lockA);
        LockOf(a.ServiceProvider.GetRequiredService<ProtectiveStopGuard>()).Should().BeSameAs(lockA);
        LockOf(b.ServiceProvider.GetRequiredService<OrderExecutionAppService>()).Should().NotBeSameAs(lockA,
            "別のホスト（並列に走る別の試験）とは待ち合わない");
    }

    [Fact]
    public async Task T_10_3008_Programの承認の受け口とS1とガードは同じ決済の直列化を受け取る()
    {
        var closeLock = new RecordingLock();
        await using var factory = new ProgramFactory(closeLock);
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        LockOf(sp.GetRequiredService<OrderExecutionAppService>()).Should().BeSameAs(closeLock, "承認の決済・保護喪失の成行手仕舞い");
        LockOf(sp.GetRequiredService<SoftwareStopExecutor>()).Should().BeSameAs(closeLock, "S1 の決済（到達の購読・ガードの撃ち直し）");
        LockOf(sp.GetRequiredService<ProtectiveStopGuard>()).Should().BeSameAs(closeLock, "ガードの成行の手仕舞い");
    }

    [Fact]
    public void T_10_3008_直列化は関係DBならadvisory_lockで試験のInMemoryならプロセス内に縮退する()
    {
        var fallback = new InProcessClosePositionLock();
        using var relational = new OrderExecutionDbContext(new DbContextOptionsBuilder<OrderExecutionDbContext>()
            .UseNpgsql("Host=localhost;Database=order_execution_svc;Username=ai;Password=ai").Options);
        PostgresClosePositionLock.ForContext(relational, fallback).Should().BeOfType<PostgresClosePositionLock>();

        using var inMemory = new OrderExecutionDbContext(new DbContextOptionsBuilder<OrderExecutionDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        PostgresClosePositionLock.ForContext(inMemory, fallback).Should().BeSameAs(fallback,
            "ホストで 1 つ（DI の singleton）。static の共有にしない（並列の試験のホストどうしが待ち合う）");
    }
}
