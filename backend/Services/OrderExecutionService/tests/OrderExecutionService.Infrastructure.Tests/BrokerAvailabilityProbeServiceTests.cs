using AiStockTrading.OrderExecution.Application.Availability;
using AiStockTrading.OrderExecution.Infrastructure.Composable.Availability;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
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

namespace AiStockTrading.OrderExecution.Infrastructure.Tests;

// FR-20, FR-05, #385, 06_daytrading-review §4.2, IADR-0150 決定1: ブローカ稼働の定期観測。
// 中核の契約は「**到達できたときだけ発行する**」——沈黙が「稼働していない」を意味する（fail-safe）。
public class BrokerAvailabilityProbeServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 14, 0, 0, TimeSpan.Zero);

    private const string ServiceName = "ai-stock-trading.order-execution-service";

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // 到達可否と発注先を制御する fake。到達可否の判定そのものは各アダプタの担当。
    private sealed class FakeProbe : IBrokerAvailabilityProbe
    {
        public bool Operational { get; set; } = true;

        public Func<Exception>? Throw { get; set; }

        public int Calls { get; private set; }

        public Task<bool> IsOperationalAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Throw is not null) throw Throw();
            return Task.FromResult(Operational);
        }
    }

    private sealed class FakeBroker(BrokerProvider provider) : IBrokerAdapter
    {
        public BrokerProvider Provider { get; } = provider;

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("稼働観測は発注しない（試し発注は統制の外側の注文になる）。");

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static Task<IHost> NewHostAsync() =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static BrokerAvailabilityProbeService NewService(
        IHost host,
        FakeProbe probe,
        bool enabled = true,
        BrokerProvider provider = BrokerProvider.MoomooSimulate) =>
        new(probe,
            new FakeBroker(provider),
            host.Services.GetRequiredService<IWolverineRuntime>(),
            new FixedTimeProvider(Now),
            Options.Create(new BrokerAvailabilityProbeOptions { Enabled = enabled }),
            NullLogger<BrokerAvailabilityProbeService>.Instance);

    private static async Task<(bool Published, ITrackedSession Session)> RunOnceAsync(
        bool operational, BrokerProvider provider = BrokerProvider.MoomooSimulate)
    {
        using var host = await NewHostAsync();
        var service = NewService(host, new FakeProbe { Operational = operational }, provider: provider);

        var published = false;
        Func<IMessageContext, Task> probeOnce = async _ =>
            published = await service.ProbeOnceAsync(CancellationToken.None);
        var session = await host.TrackActivity().ExecuteAndWaitAsync(probeOnce);

        await host.StopAsync();
        return (published, session);
    }

    // 正: 到達できた巡回は観測として発行され、**実際に接続しているアダプタの発注先**を載せる。
    [Fact]
    public async Task 到達できた巡回を観測として発行する()
    {
        var (published, session) = await RunOnceAsync(operational: true);

        published.Should().BeTrue();
        session.Sent.MessagesOf<BrokerAvailabilityObserved>().Should().Contain(m =>
            m.Provider == BrokerProvider.MoomooSimulate
            && m.ObservedAt == Now
            && m.CoveredInterval == TimeSpan.FromMinutes(5));
    }

    // **否定形（本 issue の核心）**: 到達できなければ**何も発行しない**。
    // 「稼働していなかった」をイベントで表すと、その通知が失われたときに稼働として計上され得る。
    [Fact]
    public async Task 到達できなければ何も発行しない()
    {
        var (published, session) = await RunOnceAsync(operational: false);

        published.Should().BeFalse();
        session.Sent.MessagesOf<BrokerAvailabilityObserved>().Should().BeEmpty();
    }

    // **否定形**: 内蔵 paper の稼働も観測として発行する（算入は受け手の許可制が弾く）。
    // 発注先を偽らないこと自体が、SC-03 の「paper 稼働により N 日を除外」を成り立たせる。
    [Fact]
    public async Task 内蔵paperの稼働も発注先を偽らずに発行する()
    {
        var (published, session) = await RunOnceAsync(
            operational: true, provider: BrokerProvider.InternalPaper);

        published.Should().BeTrue();
        session.Sent.MessagesOf<BrokerAvailabilityObserved>()
            .Should().Contain(m => m.Provider == BrokerProvider.InternalPaper);
    }

    // 照会例外は呼び出し側（常駐）が捕捉して次回巡回で再試行する。推測で発行しない。
    [Fact]
    public async Task 照会例外は呼び出し側へ伝播し発行しない()
    {
        using var host = await NewHostAsync();
        var probe = new FakeProbe { Throw = () => new InvalidOperationException("OpenD 不達") };
        var service = NewService(host, probe);

        var act = async () => await service.ProbeOnceAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await host.StopAsync();
    }

    // **否定形**: 無効化されていれば一度も照会せず、営業日数は 1 日も積まれない（明示的な選択である）。
    [Fact]
    public async Task 無効化されていれば一度も照会しない()
    {
        using var host = await NewHostAsync();
        var probe = new FakeProbe();
        var service = NewService(host, probe, enabled: false);

        Func<IMessageContext, Task> startAndStop = async _ =>
        {
            await service.StartAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);
        };
        var session = await host.TrackActivity().ExecuteAndWaitAsync(startAndStop);

        probe.Calls.Should().Be(0);
        session.Sent.MessagesOf<BrokerAvailabilityObserved>().Should().BeEmpty();

        await host.StopAsync();
    }

    // 巡回間隔は受け手の上限（1 件の観測が遡れる 30 分）を超えられない。
    // 超える設定を許すと「設定できるが観測が 1 件も積まれない」状態を作れてしまう。
    [Theory]
    [InlineData(0, 300)]        // 未設定・非正は既定 5 分
    [InlineData(30, 60)]        // 下限 60 秒（OpenD への連打を防ぐ）
    [InlineData(3600, 1800)]    // 上限 30 分（受け手の遡り上限と同じ）
    [InlineData(600, 600)]      // 範囲内はそのまま
    public void 巡回間隔は範囲外をクランプする(int configured, int expectedSeconds)
    {
        new BrokerAvailabilityProbeOptions { IntervalSeconds = configured }.Interval
            .Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }
}
