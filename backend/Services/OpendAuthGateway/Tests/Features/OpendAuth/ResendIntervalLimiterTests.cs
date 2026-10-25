using AwesomeAssertions;
using OpendAuthGateway.Features.OpendAuth;
using OpendAuthGateway.Tests.Infrastructure;
using Xunit;

namespace OpendAuthGateway.Tests.Features.OpendAuth;

/// <summary>
/// #1456, SC-04, IADR-0579: 再送だけの間隔（② 60 秒に 1 回）と、その外側の ① サービス全体のレート制限
/// （IADR-0322 決定 4・投入と再送の合算）との重なり。
/// </summary>
public class ResendIntervalLimiterTests
{
    private static (ResendIntervalLimiter Resend, SubmissionRateLimiter Overall) Create(
        FakeTimeProvider time, int overallMax = 5, int windowSeconds = 60, int intervalSeconds = 60)
    {
        var overall = new SubmissionRateLimiter(time, overallMax, TimeSpan.FromSeconds(windowSeconds));
        return (new ResendIntervalLimiter(time, TimeSpan.FromSeconds(intervalSeconds), overall), overall);
    }

    [Fact]
    public void 最初の再送は通す()
    {
        var (resend, _) = Create(new FakeTimeProvider());

        resend.TryAcquire(out var retryAfter).Should().Be(ResendAdmission.Accepted);
        retryAfter.Should().Be(TimeSpan.Zero);
    }

    // 受け入れ基準: 再送を 60 秒以内に 2 回送ると 2 回目が拒否される（59 秒は拒否・60 秒は許可）。
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(59, false)]
    [InlineData(60, true)]
    [InlineData(61, true)]
    public void 間隔の境界は60秒ちょうどで通す(int elapsedSeconds, bool expectedAccepted)
    {
        var time = new FakeTimeProvider();
        var (resend, _) = Create(time);
        resend.TryAcquire(out _).Should().Be(ResendAdmission.Accepted);

        time.Advance(TimeSpan.FromSeconds(elapsedSeconds));

        var admission = resend.TryAcquire(out var retryAfter);
        if (expectedAccepted)
        {
            admission.Should().Be(ResendAdmission.Accepted);
        }
        else
        {
            admission.Should().Be(ResendAdmission.IntervalNotElapsed);
            retryAfter.Should().Be(TimeSpan.FromSeconds(60 - elapsedSeconds), "画面が残り秒数を示せるよう、間隔の残りを返す");
        }
    }

    [Fact]
    public void 間隔は最後に通した再送から数える()
    {
        var time = new FakeTimeProvider();
        var (resend, _) = Create(time);
        resend.TryAcquire(out _);
        time.Advance(TimeSpan.FromSeconds(30));
        resend.TryAcquire(out _).Should().Be(ResendAdmission.IntervalNotElapsed);

        // 拒否された再送は間隔を延ばさない（拒否の 30 秒後＝最初の再送の 60 秒後に通る）。
        time.Advance(TimeSpan.FromSeconds(30));

        resend.TryAcquire(out _).Should().Be(ResendAdmission.Accepted);
    }

    [Fact]
    public void 間隔で拒否した再送は全体の枠を消費しない()
    {
        var time = new FakeTimeProvider();
        var (resend, overall) = Create(time, overallMax: 2);
        resend.TryAcquire(out _).Should().Be(ResendAdmission.Accepted);

        for (var i = 0; i < 10; i++) resend.TryAcquire(out _).Should().Be(ResendAdmission.IntervalNotElapsed);

        // ① の残り 1 件は投入のために残っている。
        overall.TryAcquire(out _).Should().BeTrue();
    }

    // ① の拒否で ② を記録すると、送っていない再送で次の再送を 60 秒待たせることになる。
    [Fact]
    public void 全体の上限で拒否した再送は間隔を記録しない()
    {
        var time = new FakeTimeProvider();
        var (resend, overall) = Create(time, overallMax: 2);
        overall.TryAcquire(out _).Should().BeTrue(); // t=0 投入
        overall.TryAcquire(out _).Should().BeTrue(); // t=0 投入（① 満杯）
        time.Advance(TimeSpan.FromSeconds(30));

        resend.TryAcquire(out var retryAfter).Should().Be(ResendAdmission.OverallLimited); // t=30
        retryAfter.Should().Be(TimeSpan.FromSeconds(30));

        // t=60: ① は空く。t=30 の拒否を ② に記録していたら、ここは t=90 まで拒否される。
        time.Advance(TimeSpan.FromSeconds(30));

        resend.TryAcquire(out _).Should().Be(ResendAdmission.Accepted, "① が空いた時点で、② は待たせない");
    }

    // 受け入れ基準: ① の合算は残る（投入と再送をあわせて数える）。
    [Fact]
    public void 通した再送は全体の枠を1件使う()
    {
        var time = new FakeTimeProvider();
        var (resend, overall) = Create(time, overallMax: 2);
        resend.TryAcquire(out _).Should().Be(ResendAdmission.Accepted);

        overall.TryAcquire(out _).Should().BeTrue();
        overall.TryAcquire(out _).Should().BeFalse("再送 1 件 ＋ 投入 1 件で上限 2 件に達している");
    }

    [Fact]
    public void 両方が塞ぐときは長いほうの残りを返す()
    {
        var time = new FakeTimeProvider();
        var (resend, overall) = Create(time, overallMax: 3, windowSeconds: 60, intervalSeconds: 20);
        overall.TryAcquire(out _).Should().BeTrue();           // t=0  投入
        time.Advance(TimeSpan.FromSeconds(10));
        overall.TryAcquire(out _).Should().BeTrue();           // t=10 投入
        resend.TryAcquire(out _).Should().Be(ResendAdmission.Accepted); // t=10 再送（① は 3 件で満杯）
        time.Advance(TimeSpan.FromSeconds(5));                 // t=15

        resend.TryAcquire(out var retryAfter).Should().Be(ResendAdmission.IntervalNotElapsed);

        // ② の残りは 15 秒（t=30）だが、① は t=60 まで空かない。示すのは後者である。
        retryAfter.Should().Be(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void 間隔は構成できる()
    {
        var time = new FakeTimeProvider();
        var (resend, _) = Create(time, intervalSeconds: 10);
        resend.Interval.Should().Be(TimeSpan.FromSeconds(10));
        resend.TryAcquire(out _);

        time.Advance(TimeSpan.FromSeconds(10));

        resend.TryAcquire(out _).Should().Be(ResendAdmission.Accepted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 間隔が0以下なら構成を拒む(int seconds)
    {
        var time = new FakeTimeProvider();
        var overall = new SubmissionRateLimiter(time, 5, TimeSpan.FromSeconds(60));

        var act = () => new ResendIntervalLimiter(time, TimeSpan.FromSeconds(seconds), overall);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void 並行する再送は1本だけが通る()
    {
        var time = new FakeTimeProvider();
        var (resend, _) = Create(time, overallMax: 100);

        var results = new ResendAdmission[64];
        Parallel.For(0, results.Length, i => results[i] = resend.TryAcquire(out _));

        results.Count(r => r == ResendAdmission.Accepted).Should().Be(1);
    }
}
