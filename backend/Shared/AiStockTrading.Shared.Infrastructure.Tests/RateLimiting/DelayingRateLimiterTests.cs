using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using FluentAssertions;
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
}
