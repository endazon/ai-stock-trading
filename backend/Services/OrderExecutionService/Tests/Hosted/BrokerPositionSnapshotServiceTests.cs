using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ObserveBrokerPositions;
using OrderExecutionService.Hosted;
using OrderExecutionService.Infrastructure.Persistence;
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

        private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>#1093: 最初の照会で完了する（常駐の初回を壁時計なしで待つ）。</summary>
        public Task FirstCall => _called.Task;

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            _called.TrySetResult();
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
        IHost host, FakePositionSource source, bool enabled = true,
        // #1093, IADR-0459: 初回の遅延を観測するときだけ差し替える（既定は固定時刻・タイマーは使わない試験）。
        TimeProvider? time = null, PositionReconciliationOptions? options = null) =>
        new(source,
            // 本アダプタは常駐（singleton）であり、Wolverine の IMessageBus（scoped）は注入できない。
            host.Services.GetRequiredService<IWolverineRuntime>(),
            time ?? new FixedTimeProvider(Now),
            Options.Create(options ?? new PositionReconciliationOptions { Enabled = enabled }),
            NullLogger<BrokerPositionSnapshotService>.Instance,
            // #880, IADR-0412: 帰属不明の検知の相乗り。本ファイルは観測の発行だけを見るので、保護記録の無い空のストアで組む
            //（検知の振る舞いは BrokerPositionSnapshotUnattributedDetectionTests が固定する）。
            BrokerPositionSnapshotUnattributedDetectionTests.DetectorScopes(
                new InMemoryProtectiveStopOrderStore(), new InMemoryExecutedOrderStore(), () => Now));

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

    // ---- T-10-1742・T-10-1744・T-10-1745: 起動直後の初回の照会をずらす（#1093 段 2） ----

    // T-10-1742, FR-10, #1093, IADR-0459 決定1・3: 初回の建玉照会の前に既定 20 秒待ち、待ちの間は照会しない。
    // ガード（即時）・稼働 probe（既定 10 秒）と同じ瞬間に重ねない（OpenD の頻度制限。IADR-0144 決定 5）。
    [Fact]
    public async Task T_10_1742_初回の照会の前に既定の遅延だけ待つ()
    {
        using var host = await NewHostAsync();
        // 照会不能（null）にして発行へ進ませない（本試験が見るのは照会の時刻だけ）。
        var source = new FakePositionSource { Result = null };
        var time = new ManualTimerTimeProvider(Now);
        var service = NewService(host, source, time: time);

        await service.StartAsync(CancellationToken.None);
        await time.FirstTimerCreated.WaitAsync(TimeSpan.FromSeconds(10));

        source.Calls.Should().Be(0, "初回の遅延が明けるまでは照会しない");
        time.DueTimes.Should().Equal([TimeSpan.FromSeconds(20)], "既定の初回の遅延は 20 秒（揺らぎなし）");

        time.FireFirst();
        await source.FirstCall.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        source.Calls.Should().Be(1, "遅延が明けたら初回の照会を 1 回行う（次は巡回間隔の後）");
        await host.StopAsync();
    }

    // T-10-1744, FR-10, #1093, IADR-0459 決定1: 待ちの間に停止要求が来たら、1 回も照会せずに正常に終わる。
    // 例外で終わると、通常の再起動のたびに常駐の異常終了として扱われる。
    [Fact]
    public async Task T_10_1744_初回の遅延の間に停止すると一度も照会せず正常に終わる()
    {
        using var host = await NewHostAsync();
        var source = new FakePositionSource();
        var time = new ManualTimerTimeProvider(Now);
        var service = NewService(host, source, time: time);

        await service.StartAsync(CancellationToken.None);
        await time.FirstTimerCreated.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        source.Calls.Should().Be(0);
        service.ExecuteTask.Should().NotBeNull();
        service.ExecuteTask!.Status.Should().Be(TaskStatus.RanToCompletion, "停止は失敗ではない");
        await host.StopAsync();
    }

    // T-10-1745, FR-10, #1093, IADR-0459 決定1: 負の値は 0（遅らせない）、巡回間隔を超える値は巡回間隔へ収める。
    [Theory]
    [InlineData(null, 600, 20)]     // 未設定は既定 20 秒
    [InlineData(-5, 600, 0)]        // 負は 0（遅らせない）
    [InlineData(0, 600, 0)]         // 0 は遅らせない（明示の無効化）
    [InlineData(45, 600, 45)]       // 範囲内はそのまま
    [InlineData(9999, 600, 600)]    // 巡回間隔を超える値は巡回間隔
    [InlineData(120, 60, 60)]       // 巡回間隔を縮めれば上限も縮む
    public void T_10_1745_初回の遅延の設定を収める(int? configured, int intervalSeconds, int expectedSeconds)
    {
        var options = new PositionReconciliationOptions { IntervalSeconds = intervalSeconds };
        if (configured is { } value) options.InitialDelaySeconds = value;

        options.InitialDelay.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    // T-10-1745, FR-10, #1093, IADR-0459 決定1: 負の設定なら待たずに初回を回す（タイマーを作らない）。
    [Fact]
    public async Task T_10_1745_負の遅延なら待たずに初回を回す()
    {
        using var host = await NewHostAsync();
        var source = new FakePositionSource { Result = null };
        var time = new ManualTimerTimeProvider(Now);
        var service = NewService(
            host, source, time: time, options: new PositionReconciliationOptions { InitialDelaySeconds = -1 });

        await service.StartAsync(CancellationToken.None);
        await source.FirstCall.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        source.Calls.Should().Be(1);
        time.DueTimes.Should().BeEmpty("遅らせない設定では初回の遅延のタイマーを作らない");
        await host.StopAsync();
    }

    // 常駐を起動して止めるだけの補助（元テストと呼び出し順は同じ）。
    private static async Task StartAndStopAsync(BrokerPositionSnapshotService service)
    {
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }
}
