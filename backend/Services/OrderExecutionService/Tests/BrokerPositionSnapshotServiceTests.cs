using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Hosted;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace OrderExecutionService.Tests;

// #292, FR-05, FR-10, IADR-0118: ブローカ建玉の定期観測。
// 中核の契約は「照会不能（null）は発行しない・空列（建玉ゼロ）は発行する」。
//
// ADR-0013, IADR-0129, #354: MassTransit のテストハーネス（IBus + harness.Published）から
// Wolverine.Tracking（IWolverineRuntime + session.Sent）へ移行した。表明の意味は同じ。
public class BrokerPositionSnapshotServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 6, 0, 0, TimeSpan.Zero);

    // 固定時刻の TimeProvider（観測時刻を決定的にする）。
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakePositionSource : IBrokerPositionSource
    {
        public IReadOnlyList<BrokerPositionSnapshot>? Result { get; set; } = [];

        public Func<Exception>? Throw { get; set; }

        public int Calls { get; private set; }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Throw is not null) throw Throw();
            return Task.FromResult(Result);
        }
    }

    private const string ServiceName = "ai-stock-trading.order-execution-service";

    // 本番と同じ配線（キュー名・fan-out・再試行・DLQ）を用い、送信先だけ stub へ倒す。
    private static Task<IHost> NewHostAsync() =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static BrokerPositionSnapshotService NewService(
        IHost host, FakePositionSource source, bool enabled = true) =>
        new(source,
            // 本アダプタは常駐（singleton）であり、Wolverine の IMessageBus（scoped）は注入できない。
            host.Services.GetRequiredService<IWolverineRuntime>(),
            new FixedTimeProvider(Now),
            Options.Create(new PositionReconciliationOptions { Enabled = enabled }),
            NullLogger<BrokerPositionSnapshotService>.Instance);

    private static async Task<(bool Published, ITrackedSession Session, FakePositionSource Source)> RunOnceAsync(
        IReadOnlyList<BrokerPositionSnapshot>? result, Func<Exception>? throws = null, bool enabled = true)
    {
        using var host = await NewHostAsync();

        var source = new FakePositionSource { Result = result, Throw = throws };
        var service = NewService(host, source, enabled);

        var published = false;
        Func<IMessageContext, Task> publishOnce = async _ =>
            published = await service.PublishOnceAsync(CancellationToken.None);
        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(publishOnce);

        await host.StopAsync();
        return (published, session, source);
    }

    [Fact]
    public async Task 観測した建玉を発行する()
    {
        var (published, session, _) = await RunOnceAsync(
            [new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 4072, 20.5m)]);

        published.Should().BeTrue();
        session.Sent.MessagesOf<BrokerPositionsObserved>()
            .Should().Contain(m => m.Positions.Count == 1 && m.ObservedAt == Now);
    }

    [Fact]
    public async Task 建玉ゼロでも観測として発行する()
    {
        // 空列は「ブローカに建玉が無い」という観測事実。発行しないと台帳側の全決済を検知できない。
        var (published, session, _) = await RunOnceAsync([]);

        published.Should().BeTrue();
        session.Sent.MessagesOf<BrokerPositionsObserved>()
            .Should().Contain(m => m.Positions.Count == 0);
    }

    [Fact]
    public async Task 照会不能なら何も発行しない()
    {
        // null（不明）を空列として発行すると、台帳の全建玉が乖離として報告される。
        var (published, session, _) = await RunOnceAsync(null);

        published.Should().BeFalse();
        session.Sent.MessagesOf<BrokerPositionsObserved>().Should().BeEmpty();
    }

    [Fact]
    public async Task 照会例外は呼び出し側へ伝播し発行しない()
    {
        // 常駐（ExecuteAsync）が捕捉して次回巡回で再試行する。ここでは推測で発行しないことを固定する。
        var act = async () => await RunOnceAsync([], throws: () => new InvalidOperationException("OpenD 不達"));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task 無効化されていれば一度も照会しない()
    {
        using var host = await NewHostAsync();
        var source = new FakePositionSource();
        var service = NewService(host, source, enabled: false);

        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(_ => StartAndStopAsync(service));

        source.Calls.Should().Be(0);
        session.Sent.MessagesOf<BrokerPositionsObserved>().Should().BeEmpty();

        await host.StopAsync();
    }

    // 常駐を起動して止めるだけの補助（元テストと呼び出し順は同じ）。
    private static async Task StartAndStopAsync(BrokerPositionSnapshotService service)
    {
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }
}
