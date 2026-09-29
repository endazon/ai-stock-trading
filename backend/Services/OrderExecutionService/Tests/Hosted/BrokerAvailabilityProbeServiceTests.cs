using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ObserveBrokerAvailability;
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

        private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>#1093: 最初の probe で完了する（常駐の初回を壁時計なしで待つ）。</summary>
        public Task FirstCall => _called.Task;

        public Task<bool> IsOperationalAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            _called.TrySetResult();
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

    // FR-19, #375, ADR-0021 決定3, IADR-0153: 口座種別の供給元。
    // **null を返す**ことが「照会失敗・種別不明」であり、発行しない側へ倒す（例外を投げない契約）。
    private sealed class FakeAccountSource : IBrokerAccountSource
    {
        public BrokerAccountState? State { get; set; } = new(AccountType.Margin);

        public int Calls { get; private set; }

        public Task<BrokerAccountState?> GetAccountStateAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(State);
        }
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
        BrokerProvider provider = BrokerProvider.MoomooSimulate,
        // #375: 口座種別の供給元。**未登録（null）が既定**である——内蔵 paper 構成では登録されない。
        IBrokerAccountSource? accountSource = null,
        // #1093, IADR-0459: 初回の遅延を観測するときだけ差し替える（既定は固定時刻・タイマーは使わない試験）。
        TimeProvider? time = null,
        BrokerAvailabilityProbeOptions? options = null) =>
        new(probe,
            new FakeBroker(provider),
            host.Services.GetRequiredService<IWolverineRuntime>(),
            time ?? new FixedTimeProvider(Now),
            Options.Create(options ?? new BrokerAvailabilityProbeOptions { Enabled = enabled }),
            NullLogger<BrokerAvailabilityProbeService>.Instance,
            accountSource);

    private static async Task<(bool Published, ITrackedSession Session)> RunOnceAsync(
        bool operational,
        BrokerProvider provider = BrokerProvider.MoomooSimulate,
        IBrokerAccountSource? accountSource = null)
    {
        using var host = await NewHostAsync();
        var service = NewService(
            host, new FakeProbe { Operational = operational }, provider: provider, accountSource: accountSource);

        var published = false;
        Func<IMessageContext, Task> probeOnce = async _ =>
            published = await service.ProbeOnceAsync(CancellationToken.None);
        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(probeOnce);

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

    // =====================================================================================
    // FR-19, #375, ADR-0021 決定3, IADR-0153: 口座種別の観測（同じ巡回に相乗りする）
    //
    // 契約は稼働観測と同じで「**判明したときだけ発行する**」。到達不能・照会失敗・種別不明はいずれも
    // 「発行しない」であり、受け手は沈黙を「口座種別を確認できていない」として新規建てを止める。
    // =====================================================================================

    [Fact]
    public async Task 口座種別が判明した巡回は口座観測も発行する()
    {
        var source = new FakeAccountSource { State = new BrokerAccountState(AccountType.Cash, 1_000m) };

        var (published, session) = await RunOnceAsync(operational: true, accountSource: source);

        published.Should().BeTrue();
        session.Sent.MessagesOf<BrokerAccountObserved>().Should().Contain(m =>
            m.Provider == BrokerProvider.MoomooSimulate
            && m.Account.AccountType == AccountType.Cash
            && m.Account.SettledCashInBase == 1_000m
            && m.ObservedAt == Now);
    }

    // **否定形（本 issue の核心）**: 口座種別が不明なら**発行しない**。
    // 「不明である」をイベントで表すと、その通知が失われたとき（プロセス断・ネットワーク断）に
    // 古い観測が生き残り、現金口座なのに信用口座の緩い統制で回る。沈黙が安全側に倒れる向きを選ぶ。
    [Fact]
    public async Task 口座種別が不明なら口座観測を発行しない()
    {
        var source = new FakeAccountSource { State = null };

        var (published, session) = await RunOnceAsync(operational: true, accountSource: source);

        // 稼働観測そのものは発行される（到達はできているため）。口座観測だけが落ちる。
        published.Should().BeTrue();
        session.Sent.MessagesOf<BrokerAvailabilityObserved>().Should().NotBeEmpty();
        session.Sent.MessagesOf<BrokerAccountObserved>().Should().BeEmpty();
    }

    // **否定形**: 到達できなければ口座種別を照会すらしない（稼働観測と同時に落ちる）。
    [Fact]
    public async Task 到達できなければ口座種別を照会しない()
    {
        var source = new FakeAccountSource();

        var (published, session) = await RunOnceAsync(operational: false, accountSource: source);

        published.Should().BeFalse();
        source.Calls.Should().Be(0);
        session.Sent.MessagesOf<BrokerAccountObserved>().Should().BeEmpty();
    }

    // **否定形（IADR-0153 決定2）**: 供給元が未登録（内蔵 paper 構成）なら口座観測は発行されない。
    // 外部へ一度も発注しない擬似約定にはブローカー口座が存在せず、判定側も口座種別を要求しない。
    [Fact]
    public async Task 供給元が未登録なら口座観測を発行しない()
    {
        var (published, session) = await RunOnceAsync(
            operational: true, provider: BrokerProvider.InternalPaper, accountSource: null);

        published.Should().BeTrue();
        session.Sent.MessagesOf<BrokerAvailabilityObserved>().Should().NotBeEmpty();
        session.Sent.MessagesOf<BrokerAccountObserved>().Should().BeEmpty();
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
        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(startAndStop);

        probe.Calls.Should().Be(0);
        session.Sent.MessagesOf<BrokerAvailabilityObserved>().Should().BeEmpty();

        await host.StopAsync();
    }

    // ---- T-10-1743・T-10-1744・T-10-1745: 起動直後の初回の probe をずらす（#1093 段 2） ----

    // T-10-1743, FR-10, FR-20, #1093, IADR-0459 決定1・3: 初回の probe（建玉照会を流用する）の前に既定 10 秒待ち、
    // 待ちの間は照会しない。ガード（即時）・スナップショット（既定 20 秒）と同じ瞬間に重ねない。
    [Fact]
    public async Task T_10_1743_初回のprobeの前に既定の遅延だけ待つ()
    {
        using var host = await NewHostAsync();
        // 到達不能にして発行へ進ませない（本試験が見るのは照会の時刻だけ）。
        var probe = new FakeProbe { Operational = false };
        var time = new ManualTimerTimeProvider(Now);
        var service = NewService(host, probe, time: time);

        await service.StartAsync(CancellationToken.None);
        await time.FirstTimerCreated.WaitAsync(TimeSpan.FromSeconds(10));

        probe.Calls.Should().Be(0, "初回の遅延が明けるまでは照会しない");
        time.DueTimes.Should().Equal([TimeSpan.FromSeconds(10)], "既定の初回の遅延は 10 秒（揺らぎなし）");

        time.FireFirst();
        await probe.FirstCall.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        probe.Calls.Should().Be(1, "遅延が明けたら初回の probe を 1 回行う（次は巡回間隔の後）");
        await host.StopAsync();
    }

    // T-10-1744, FR-10, #1093, IADR-0459 決定1: 待ちの間に停止要求が来たら、1 回も照会せずに正常に終わる。
    [Fact]
    public async Task T_10_1744_初回の遅延の間に停止すると一度も照会せず正常に終わる()
    {
        using var host = await NewHostAsync();
        var probe = new FakeProbe();
        var time = new ManualTimerTimeProvider(Now);
        var service = NewService(host, probe, time: time);

        await service.StartAsync(CancellationToken.None);
        await time.FirstTimerCreated.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        probe.Calls.Should().Be(0);
        service.ExecuteTask.Should().NotBeNull();
        service.ExecuteTask!.Status.Should().Be(TaskStatus.RanToCompletion, "停止は失敗ではない");
        await host.StopAsync();
    }

    // T-10-1745, FR-10, #1093, IADR-0459 決定1: 負の値は 0（遅らせない）、巡回間隔を超える値は巡回間隔へ収める。
    [Theory]
    [InlineData(null, 300, 10)]     // 未設定は既定 10 秒
    [InlineData(-5, 300, 0)]        // 負は 0（遅らせない）
    [InlineData(0, 300, 0)]         // 0 は遅らせない（明示の無効化）
    [InlineData(45, 300, 45)]       // 範囲内はそのまま
    [InlineData(9999, 300, 300)]    // 巡回間隔を超える値は巡回間隔
    [InlineData(120, 60, 60)]       // 巡回間隔を縮めれば上限も縮む
    public void T_10_1745_初回の遅延の設定を収める(int? configured, int intervalSeconds, int expectedSeconds)
    {
        var options = new BrokerAvailabilityProbeOptions { IntervalSeconds = intervalSeconds };
        if (configured is { } value) options.InitialDelaySeconds = value;

        options.InitialDelay.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    // T-10-1745, FR-10, #1093, IADR-0459 決定1: 負の設定なら待たずに初回を回す（タイマーを作らない）。
    [Fact]
    public async Task T_10_1745_負の遅延なら待たずに初回を回す()
    {
        using var host = await NewHostAsync();
        var probe = new FakeProbe { Operational = false };
        var time = new ManualTimerTimeProvider(Now);
        var service = NewService(
            host, probe, time: time, options: new BrokerAvailabilityProbeOptions { InitialDelaySeconds = -1 });

        await service.StartAsync(CancellationToken.None);
        await probe.FirstCall.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        probe.Calls.Should().Be(1);
        time.DueTimes.Should().BeEmpty("遅らせない設定では初回の遅延のタイマーを作らない");
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
