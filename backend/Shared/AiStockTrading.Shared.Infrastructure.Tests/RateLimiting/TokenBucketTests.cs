using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.RateLimiting;

// FR-01, ADR-0004, IADR-0064: レート制限のトークンバケット（純粋な状態機械・時計を持たない）を境界値で検証する。
// 各情報源の公表上限（Finnhub 60回/分・SEC EDGAR 10回/秒 等）より保守側の既定で送信前に自制するための土台。
public class TokenBucketTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 容量ぶんは連続して消費できる()
    {
        var bucket = new TokenBucket(capacity: 3, refillInterval: TimeSpan.FromMinutes(1));

        for (var i = 0; i < 3; i++)
            bucket.TryConsume(T0, out _).Should().BeTrue($"{i + 1} 件目は容量 3 の範囲内");
    }

    [Fact]
    public void 容量を超えると拒否し次に補充されるまでの待機時間を返す()
    {
        // 1 分あたり 3 トークン＝1 トークンの補充に 20 秒。
        var bucket = new TokenBucket(capacity: 3, refillInterval: TimeSpan.FromMinutes(1));
        for (var i = 0; i < 3; i++)
            bucket.TryConsume(T0, out _);

        bucket.TryConsume(T0, out var retryAfter).Should().BeFalse();
        retryAfter.Should().Be(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void 待機時間の経過後は再び消費できる()
    {
        var bucket = new TokenBucket(capacity: 3, refillInterval: TimeSpan.FromMinutes(1));
        for (var i = 0; i < 3; i++)
            bucket.TryConsume(T0, out _);
        bucket.TryConsume(T0, out var retryAfter);

        bucket.TryConsume(T0 + retryAfter, out _).Should().BeTrue();
    }

    [Fact]
    public void 補充は容量を上限に頭打ちになる()
    {
        var bucket = new TokenBucket(capacity: 2, refillInterval: TimeSpan.FromSeconds(2));
        bucket.TryConsume(T0, out _);
        bucket.TryConsume(T0, out _);

        // 十分に時間が経っても、貯まるのは容量ぶん（2 件）まで。3 件目は拒否される。
        var later = T0.AddHours(1);
        bucket.TryConsume(later, out _).Should().BeTrue();
        bucket.TryConsume(later, out _).Should().BeTrue();
        bucket.TryConsume(later, out _).Should().BeFalse();
    }

    [Fact]
    public void 部分的な経過時間でも比例して補充される()
    {
        // 1 秒あたり 10 トークン＝1 トークンの補充に 100ms（SEC EDGAR 相当の毎秒制限）。
        var bucket = new TokenBucket(capacity: 10, refillInterval: TimeSpan.FromSeconds(1));
        for (var i = 0; i < 10; i++)
            bucket.TryConsume(T0, out _);

        bucket.TryConsume(T0.AddMilliseconds(100), out _).Should().BeTrue();
        bucket.TryConsume(T0.AddMilliseconds(100), out _).Should().BeFalse();
    }

    [Fact]
    public void 時刻が巻き戻っても補充されない()
    {
        // 単調でない時刻（NTP 補正等）で不当にトークンが増えないこと（安全側＝拒否）。
        var bucket = new TokenBucket(capacity: 1, refillInterval: TimeSpan.FromMinutes(1));
        bucket.TryConsume(T0, out _).Should().BeTrue();

        bucket.TryConsume(T0.AddMinutes(-5), out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 容量は1以上でなければならない(int capacity)
    {
        var act = () => new TokenBucket(capacity, TimeSpan.FromMinutes(1));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void 補充間隔は正でなければならない()
    {
        var act = () => new TokenBucket(1, TimeSpan.Zero);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
