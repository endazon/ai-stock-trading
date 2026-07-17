using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.InformationCollection.Domain.RateLimiting;
using AiStockTrading.InformationCollection.Worker.Composable.RateLimiting;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.InformationCollection.Worker.Tests;

// FR-01, ADR-0004, IADR-0065: 送信前レート制限アダプタ。実時間を待たず、フェイク時計＋フェイク待機で決定的に検証する。
public class DelayingRateLimiterTests
{
    [Fact]
    public async Task 容量内は待機せずに通す()
    {
        var clock = new FakeClock();
        var delays = new List<TimeSpan>();
        var limiter = Create(capacity: 2, TimeSpan.FromMinutes(1), clock, delays);

        await limiter.WaitAsync();
        await limiter.WaitAsync();

        delays.Should().BeEmpty();
    }

    [Fact]
    public async Task 容量超過は補充までの時間だけ待ってから通す()
    {
        // 1 分あたり 2 トークン＝1 トークンの補充に 30 秒。
        var clock = new FakeClock();
        var delays = new List<TimeSpan>();
        var limiter = Create(capacity: 2, TimeSpan.FromMinutes(1), clock, delays);
        await limiter.WaitAsync();
        await limiter.WaitAsync();

        await limiter.WaitAsync();

        delays.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task 待機はキャンセルできる()
    {
        var clock = new FakeClock();
        var limiter = Create(capacity: 1, TimeSpan.FromMinutes(1), clock, delays: null);
        await limiter.WaitAsync();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => limiter.WaitAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // 待機した時間だけ時計を進めるフェイク待機で、待機後に必ず通る（無限ループしない）ことを確かめる。
    private static DelayingRateLimiter Create(
        int capacity, TimeSpan refillInterval, FakeClock clock, List<TimeSpan>? delays) =>
        new(new TokenBucket(capacity, refillInterval), clock, (delay, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            delays?.Add(delay);
            clock.Advance(delay);
            return Task.CompletedTask;
        });

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
