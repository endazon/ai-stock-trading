using AwesomeAssertions;
using OpendAuthGateway.Features.OpendAuth;
using OpendAuthGateway.Tests.Infrastructure;
using Xunit;

namespace OpendAuthGateway.Tests.Features.OpendAuth;

/// <summary>
/// #722, IADR-0322 決定 4: 投入の流量制限。
/// 守っているのは moomoo の SMS 送信枠（<c>resend</c>）と総当たり耐性である。
/// </summary>
public class SubmissionRateLimiterTests
{
    private static SubmissionRateLimiter Create(FakeTimeProvider time, int max = 3, int windowSeconds = 60)
        => new(time, max, TimeSpan.FromSeconds(windowSeconds));

    [Fact]
    public void 窓の上限までは通す()
    {
        var time = new FakeTimeProvider();
        var limiter = Create(time);

        for (var i = 0; i < 3; i++) limiter.TryAcquire(out _).Should().BeTrue($"{i + 1} 件目は上限内");
    }

    [Fact]
    public void 上限を超えたら止める()
    {
        var time = new FakeTimeProvider();
        var limiter = Create(time);
        for (var i = 0; i < 3; i++) limiter.TryAcquire(out _);

        limiter.TryAcquire(out var retryAfter).Should().BeFalse();
        retryAfter.Should().BePositive("いつ再試行できるかを呼び出し元へ返す");
    }

    [Fact]
    public void 窓を跨げばまた通す()
    {
        var time = new FakeTimeProvider();
        var limiter = Create(time);
        for (var i = 0; i < 3; i++) limiter.TryAcquire(out _);
        limiter.TryAcquire(out _).Should().BeFalse();

        time.Advance(TimeSpan.FromSeconds(60));

        limiter.TryAcquire(out _).Should().BeTrue();
    }

    [Fact]
    public void 窓の途中では回復しない()
    {
        var time = new FakeTimeProvider();
        var limiter = Create(time);
        for (var i = 0; i < 3; i++) limiter.TryAcquire(out _);

        time.Advance(TimeSpan.FromSeconds(59));

        limiter.TryAcquire(out _).Should().BeFalse();
    }

    [Fact]
    public void 上限と窓は構成できる()
    {
        var time = new FakeTimeProvider();
        var limiter = Create(time, max: 1, windowSeconds: 10);

        limiter.MaxSubmissions.Should().Be(1);
        limiter.Window.Should().Be(TimeSpan.FromSeconds(10));
        limiter.TryAcquire(out _).Should().BeTrue();
        limiter.TryAcquire(out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 上限が0以下なら構成を拒む(int max)
    {
        var act = () => new SubmissionRateLimiter(new FakeTimeProvider(), max, TimeSpan.FromSeconds(60));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void 窓が0以下なら構成を拒む()
    {
        var act = () => new SubmissionRateLimiter(new FakeTimeProvider(), 1, TimeSpan.Zero);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // #1456, IADR-0579: 再送だけの間隔が残り秒数を「両方が空くまで」にするための覗き見。
    [Fact]
    public void 待ちの覗き見は枠を消費しない()
    {
        var time = new FakeTimeProvider();
        var limiter = Create(time);
        limiter.TryAcquire(out _);
        limiter.TryAcquire(out _);

        for (var i = 0; i < 10; i++) limiter.CurrentWait().Should().Be(TimeSpan.Zero, "まだ 1 件取れる");

        limiter.TryAcquire(out _).Should().BeTrue("覗き見は 3 件目の枠を使っていない");
    }

    [Fact]
    public void 待ちの覗き見は最も古い1件が窓から外れるまでを返す()
    {
        var time = new FakeTimeProvider();
        var limiter = Create(time);
        for (var i = 0; i < 3; i++) limiter.TryAcquire(out _);
        time.Advance(TimeSpan.FromSeconds(15));

        limiter.CurrentWait().Should().Be(TimeSpan.FromSeconds(45));
        limiter.TryAcquire(out var retryAfter).Should().BeFalse();
        retryAfter.Should().Be(limiter.CurrentWait(), "取得の拒否と同じ値を返す");
    }
}
