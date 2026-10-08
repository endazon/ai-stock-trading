using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.RateLimiting;

// FR-01, ADR-0004, IADR-0064: 送信前レート制限アダプタ。実時間を待たず、フェイク時計＋フェイク待機で決定的に検証する。
// IADR-0068: 共有物への移動に伴い時計を IClock → TimeProvider へ替えた。検証内容は移動前から不変。
public class DelayingRateLimiterTests
{
    [Fact]
    public async Task 容量内は待機せずに通す()
    {
        var time = new FakeTimeProvider();
        var delays = new List<TimeSpan>();
        var limiter = Create(capacity: 2, TimeSpan.FromMinutes(1), time, delays);

        await limiter.WaitAsync();
        await limiter.WaitAsync();

        delays.Should().BeEmpty();
    }

    [Fact]
    public async Task 容量超過は補充までの時間だけ待ってから通す()
    {
        // 1 分あたり 2 トークン＝1 トークンの補充に 30 秒。
        var time = new FakeTimeProvider();
        var delays = new List<TimeSpan>();
        var limiter = Create(capacity: 2, TimeSpan.FromMinutes(1), time, delays);
        await limiter.WaitAsync();
        await limiter.WaitAsync();

        await limiter.WaitAsync();

        delays.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task 待機はキャンセルできる()
    {
        var time = new FakeTimeProvider();
        var limiter = Create(capacity: 1, TimeSpan.FromMinutes(1), time, delays: null);
        await limiter.WaitAsync();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => limiter.WaitAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // T-10-2460, FR-04, NFR-01, ADR-0043, #1251（PR #1250 の監査 🟢-3）: 待機を注入しないとき、既定の待機は
    // TimeProvider のタイマーで待つ（Task.Delay(d, timeProvider, ct)）。偽の時計のタイマーを発火させるまで通らず、
    // 発火させると通る（実時間は待たない）。是正前の既定（Task.Delay(d, ct)）では偽の時計のタイマーが作られず、待機が実時間に依る。
    [Fact]
    public async Task T_10_2460_既定の待機はTimeProviderのタイマーで待つ()
    {
        var time = new ManualTimerTimeProvider();
        var limiter = new DelayingRateLimiter(new TokenBucket(1, TimeSpan.FromMinutes(1)), time);
        await limiter.WaitAsync();

        var waiting = limiter.WaitAsync();

        waiting.IsCompleted.Should().BeFalse("補充（60 秒）まで待つ");
        time.Timers.Should().ContainSingle().Which.DueTime.Should().BeCloseTo(TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(1));

        time.FireAll();
        await waiting;

        waiting.IsCompletedSuccessfully.Should().BeTrue();
    }

    // 待機した時間だけ時計を進めるフェイク待機で、待機後に必ず通る（無限ループしない）ことを確かめる。
    private static DelayingRateLimiter Create(
        int capacity, TimeSpan refillInterval, FakeTimeProvider time, List<TimeSpan>? delays) =>
        new(new TokenBucket(capacity, refillInterval), time, (delay, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            delays?.Add(delay);
            time.Advance(delay);
            return Task.CompletedTask;
        });

    // Microsoft.Extensions.Time.Testing は中央パッケージ管理に未登録のため、最小の偽装で足す
    // （MarketDataSourceTests と同じ方針）。
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    // T-10-2460: CreateTimer を上書きし、発火を手で起こす偽の時計（発火の時刻まで現在時刻を進める）。
    private sealed class ManualTimerTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

        public List<ManualTimer> Timers { get; } = [];

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            Timers.Add(timer);
            return timer;
        }

        public void FireAll()
        {
            foreach (var timer in Timers.ToList())
            {
                _now += timer.DueTime;
                timer.Fire();
            }
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        private bool _disposed;

        public TimeSpan DueTime { get; } = dueTime;

        public void Fire()
        {
            if (!_disposed)
                callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;

        public void Dispose() => _disposed = true;

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
