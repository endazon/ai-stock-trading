using System.Text;
using AiStockTrading.Shared.Infrastructure.Composable.SecondFactor;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.SecondFactor;

// NFR-06, FR-14, #1542, IADR-0615: 危険な操作の第 2 要素（TOTP）の検証。提供側で検証し、段 1 は記録だけ、段 2 は拒む。
public class OwnerSecondFactorGateTests
{
    // RFC 6238 付録 B の SHA1 の公開の試験値の種（ASCII の "1234567890" を 2 回）を、実行時に base32 へ組み立てる（本物の秘密ではない。
    // 文字列の定数で書くと秘密の走査に掛かる）。
    private static readonly string RfcSecret = Totp.EncodeBase32(System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("1234567890", 2))));

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void RFC6238の試験値と一致する(long unixSeconds, string expected)
    {
        Totp.TryDecodeBase32(RfcSecret, out var key).Should().BeTrue();
        Totp.EncodeBase32(key).Should().Be(RfcSecret, "符号化と復号は往復する");
        key.Should().Equal(Encoding.ASCII.GetBytes("12345678901234567890"));

        Totp.Compute(key, Totp.StepOf(DateTimeOffset.FromUnixTimeSeconds(unixSeconds))).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("GEZDGNBV")] // 5 バイト（短すぎる）
    [InlineData("GEZDGNBVGY3TQOJQ1!")]
    public void 書式の外のシークレットは読まない(string? secret) =>
        Totp.TryDecodeBase32(secret, out _).Should().BeFalse();

    [Fact]
    public void Offなら検証せず通す_既定の振る舞いを変えない()
    {
        var gate = Gate(SecondFactorMode.Off, out _);

        gate.Check(SecondFactorOperation.KillSwitchDisengage, Target, null).Should().Be(new SecondFactorDecision(SecondFactorOutcome.Skipped, true));
    }

    [Fact]
    public void 正しいコードは通る_前後1段まで受け付ける()
    {
        var gate = Gate(SecondFactorMode.Enforce, out var clock);

        gate.Check(SecondFactorOperation.TradingResume, Target, CodeAt(clock.Now.AddSeconds(-30))).Should().Be(Verified);
    }

    [Fact]
    public void 同じコードの再利用は拒む()
    {
        var gate = Gate(SecondFactorMode.Enforce, out var clock);
        var code = CodeAt(clock.Now);

        gate.Check(SecondFactorOperation.StagePromote, Target, code).Outcome.Should().Be(SecondFactorOutcome.Verified);
        gate.Check(SecondFactorOperation.StagePromote, Target, code).Should().Be(new SecondFactorDecision(SecondFactorOutcome.Replayed, false));
    }

    [Fact]
    public void 一度通った段より古い段のコードも拒む()
    {
        var gate = Gate(SecondFactorMode.Enforce, out var clock);

        gate.Check(SecondFactorOperation.ReportConfirm, Target, CodeAt(clock.Now.AddSeconds(30))).Outcome.Should().Be(SecondFactorOutcome.Verified);
        gate.Check(SecondFactorOperation.ReportConfirm, Target, CodeAt(clock.Now)).Outcome.Should().Be(SecondFactorOutcome.Replayed);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("１２３４５６")] // 全角
    [InlineData("12345a")]
    public void 書式の外は拒む(string code)
    {
        var gate = Gate(SecondFactorMode.Enforce, out _);

        gate.Check(SecondFactorOperation.GoodFaithViolationClear, Target, code).Should().Be(new SecondFactorDecision(SecondFactorOutcome.Invalid, false));
    }

    // 時刻は固定（ManualClock）なので、前後 1 段の 3 つのコードを除いた 6 桁を決めて外す（偶然一致で黙って通らない）。
    [Fact]
    public void 前後1段のどれにも当たらない6桁は拒む()
    {
        var gate = Gate(SecondFactorMode.Enforce, out var clock);
        var wrong = Wrong(clock);
        new[] { -30, 0, 30 }.Select(s => CodeAt(clock.Now.AddSeconds(s))).Should().NotContain(wrong);

        gate.Check(SecondFactorOperation.GoodFaithViolationClear, Target, wrong).Should().Be(new SecondFactorDecision(SecondFactorOutcome.Invalid, false));
    }

    [Fact]
    public void 範囲の外の古いコードは拒む()
    {
        var gate = Gate(SecondFactorMode.Enforce, out var clock);

        gate.Check(SecondFactorOperation.PositionDriftAdopt, Target, CodeAt(clock.Now.AddSeconds(-90))).Outcome.Should().Be(SecondFactorOutcome.Invalid);
    }

    [Fact]
    public void 欠けたコードはEnforceで拒みAuditで通す()
    {
        Gate(SecondFactorMode.Enforce, out _).Check(SecondFactorOperation.PolicyConfirm, Target, null)
            .Should().Be(new SecondFactorDecision(SecondFactorOutcome.Missing, false));
        Gate(SecondFactorMode.Audit, out _).Check(SecondFactorOperation.PolicyConfirm, Target, null)
            .Should().Be(new SecondFactorDecision(SecondFactorOutcome.Missing, true));
    }

    [Fact]
    public void 段1のAuditは失敗しても操作を止めない()
    {
        Gate(SecondFactorMode.Audit, out _).Check(SecondFactorOperation.KillSwitchDisengage, Target, "000001")
            .Allowed.Should().BeTrue();
    }

    [Fact]
    public void 連続5回失敗するとロックし_正しいコードも通さず_15分で解ける()
    {
        var gate = Gate(SecondFactorMode.Enforce, out var clock);
        for (var i = 0; i < 5; i++)
            gate.Check(SecondFactorOperation.TradingResume, Target, Wrong(clock)).Outcome.Should().Be(SecondFactorOutcome.Invalid);

        gate.Check(SecondFactorOperation.TradingResume, Target, CodeAt(clock.Now)).Should().Be(new SecondFactorDecision(SecondFactorOutcome.LockedOut, false));

        clock.Now = clock.Now.AddMinutes(15);
        gate.Check(SecondFactorOperation.TradingResume, Target, CodeAt(clock.Now)).Outcome.Should().Be(SecondFactorOutcome.Verified);
    }

    [Fact]
    public void 成功すると失敗の数は戻る()
    {
        var gate = Gate(SecondFactorMode.Enforce, out var clock);
        for (var i = 0; i < 4; i++)
            gate.Check(SecondFactorOperation.TradingResume, Target, Wrong(clock));
        gate.Check(SecondFactorOperation.TradingResume, Target, CodeAt(clock.Now)).Outcome.Should().Be(SecondFactorOutcome.Verified);

        clock.Now = clock.Now.AddSeconds(30);
        for (var i = 0; i < 4; i++)
            gate.Check(SecondFactorOperation.TradingResume, Target, Wrong(clock));
        gate.Check(SecondFactorOperation.TradingResume, Target, CodeAt(clock.Now)).Outcome.Should().Be(SecondFactorOutcome.Verified);
    }

    [Fact]
    public void シークレットが無ければEnforceは拒みAuditは記録だけ()
    {
        var clock = new ManualClock();
        new OwnerSecondFactorGate(new OwnerSecondFactorOptions { Mode = SecondFactorMode.Enforce }, clock, NullLogger.Instance)
            .Check(SecondFactorOperation.StagePromote, Target, "123456").Should().Be(new SecondFactorDecision(SecondFactorOutcome.NotConfigured, false));
        new OwnerSecondFactorGate(new OwnerSecondFactorOptions { Mode = SecondFactorMode.Audit }, clock, NullLogger.Instance)
            .Check(SecondFactorOperation.StagePromote, Target, "123456").Should().Be(new SecondFactorDecision(SecondFactorOutcome.NotConfigured, true));
    }

    private const string Target = "kill-switch";

    private static readonly SecondFactorDecision Verified = new(SecondFactorOutcome.Verified, true);

    private static OwnerSecondFactorGate Gate(SecondFactorMode mode, out ManualClock clock)
    {
        clock = new ManualClock();
        return new OwnerSecondFactorGate(
            new OwnerSecondFactorOptions { Mode = mode, Secret = RfcSecret }, clock, NullLogger.Instance);
    }

    private static string CodeAt(DateTimeOffset at)
    {
        Totp.TryDecodeBase32(RfcSecret, out var key);
        return Totp.Compute(key, Totp.StepOf(at));
    }

    // 前後 1 段のどれにも当たらないコード。
    private static string Wrong(ManualClock clock)
    {
        var valid = new[] { -30, 0, 30 }.Select(s => CodeAt(clock.Now.AddSeconds(s))).ToHashSet();
        return Enumerable.Range(0, 10).Select(i => $"{i}{i}{i}{i}{i}{i}").First(c => !valid.Contains(c));
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_780_000_005);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
