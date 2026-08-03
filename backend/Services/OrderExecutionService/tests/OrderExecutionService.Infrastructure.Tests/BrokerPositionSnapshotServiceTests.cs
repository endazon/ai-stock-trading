using AiStockTrading.OrderExecution.Application.Reconciliation;
using AiStockTrading.OrderExecution.Infrastructure.Composable.Reconciliation;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiStockTrading.OrderExecution.Infrastructure.Tests;

// #292, FR-05, FR-10, IADR-0118: ブローカ建玉の定期観測。
// 中核の契約は「照会不能（null）は発行しない・空列（建玉ゼロ）は発行する」。
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

    private static async Task<(bool Published, ITestHarness Harness, FakePositionSource Source)> RunOnceAsync(
        IReadOnlyList<BrokerPositionSnapshot>? result, Func<Exception>? throws = null, bool enabled = true)
    {
        var provider = new ServiceCollection().AddMassTransitTestHarness().BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var source = new FakePositionSource { Result = result, Throw = throws };
        var service = new BrokerPositionSnapshotService(
            source,
            provider.GetRequiredService<IBus>(),
            new FixedTimeProvider(Now),
            Options.Create(new PositionReconciliationOptions { Enabled = enabled }),
            NullLogger<BrokerPositionSnapshotService>.Instance);

        var published = await service.PublishOnceAsync(CancellationToken.None);
        return (published, harness, source);
    }

    [Fact]
    public async Task 観測した建玉を発行する()
    {
        var (published, harness, _) = await RunOnceAsync(
            [new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 4072, 20.5m)]);

        published.Should().BeTrue();
        (await harness.Published.Any<BrokerPositionsObserved>(
            c => c.Context.Message.Positions.Count == 1
              && c.Context.Message.ObservedAt == Now)).Should().BeTrue();
    }

    [Fact]
    public async Task 建玉ゼロでも観測として発行する()
    {
        // 空列は「ブローカに建玉が無い」という観測事実。発行しないと台帳側の全決済を検知できない。
        var (published, harness, _) = await RunOnceAsync([]);

        published.Should().BeTrue();
        (await harness.Published.Any<BrokerPositionsObserved>(
            c => c.Context.Message.Positions.Count == 0)).Should().BeTrue();
    }

    [Fact]
    public async Task 照会不能なら何も発行しない()
    {
        // null（不明）を空列として発行すると、台帳の全建玉が乖離として報告される。
        var (published, harness, _) = await RunOnceAsync(null);

        published.Should().BeFalse();
        (await harness.Published.Any<BrokerPositionsObserved>()).Should().BeFalse();
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
        var provider = new ServiceCollection().AddMassTransitTestHarness().BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var source = new FakePositionSource();
        var service = new BrokerPositionSnapshotService(
            source,
            provider.GetRequiredService<IBus>(),
            new FixedTimeProvider(Now),
            Options.Create(new PositionReconciliationOptions { Enabled = false }),
            NullLogger<BrokerPositionSnapshotService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        source.Calls.Should().Be(0);
        (await harness.Published.Any<BrokerPositionsObserved>()).Should().BeFalse();
    }
}
