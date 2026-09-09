using AwesomeAssertions;
using OpendAuthGateway.Features.OpendAuth;
using OpendAuthGateway.Tests.Infrastructure;
using Xunit;

namespace OpendAuthGateway.Tests.Features.OpendAuth;

/// <summary>
/// #722, IADR-0320 決定 4: 投入の流量制限。
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
}
