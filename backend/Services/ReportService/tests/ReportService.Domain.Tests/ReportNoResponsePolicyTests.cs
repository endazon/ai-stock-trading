using ReportService.Domain;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Domain.Tests;

// FR-07, UC-03〜05, ADR-0003, IADR-0071 決定2: 無応答時の既定動作（純関数・決定的）。
// 翌営業日開場（deadline）までに提示済みドラフトへの応答（承認）が無い場合の挙動を安全側で定義する。
// 既定は「直近の確定済み方針を継続」（＝確定を経ない新方針は自動適用しない・ADR-0003）。設定で「停止」に変更可。
public class ReportNoResponsePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 提示待ちが無ければ_継続_現行の定常状態()
    {
        // 承認待ちのレビューが無い＝定常状態。直近の確定済み方針を継続する。
        ReportNoResponsePolicy.Decide(Now, Now.AddHours(24), hasPendingReview: false, NoResponseBehavior.ContinueLastConfirmed)
            .Should().Be(NoResponseOutcome.ContinueLastConfirmed);
        ReportNoResponsePolicy.Decide(Now, Now.AddHours(24), hasPendingReview: false, NoResponseBehavior.Halt)
            .Should().Be(NoResponseOutcome.ContinueLastConfirmed);
    }

    [Fact]
    public void 提示待ちで期限内は_応答待ち_何もしない()
    {
        ReportNoResponsePolicy.Decide(Now, Now.AddHours(1), hasPendingReview: true, NoResponseBehavior.ContinueLastConfirmed)
            .Should().Be(NoResponseOutcome.AwaitingApproval);
        ReportNoResponsePolicy.Decide(Now, Now.AddHours(1), hasPendingReview: true, NoResponseBehavior.Halt)
            .Should().Be(NoResponseOutcome.AwaitingApproval);
    }

    [Fact]
    public void 提示待ちで期限超過_無応答_既定は継続()
    {
        // 期限（翌営業日開場）を過ぎても無応答: 既定は直近の確定済み方針を継続（安全側・運用の連続性）。
        ReportNoResponsePolicy.Decide(Now, Now.AddHours(-1), hasPendingReview: true, NoResponseBehavior.ContinueLastConfirmed)
            .Should().Be(NoResponseOutcome.ContinueLastConfirmed);
    }

    [Fact]
    public void 提示待ちで期限超過_無応答_停止設定なら停止()
    {
        ReportNoResponsePolicy.Decide(Now, Now.AddHours(-1), hasPendingReview: true, NoResponseBehavior.Halt)
            .Should().Be(NoResponseOutcome.Halt);
    }

    [Fact]
    public void 期限ちょうどは_超過扱い_境界()
    {
        // now == deadline は「開場までに応答が無かった」＝超過扱い。
        ReportNoResponsePolicy.Decide(Now, Now, hasPendingReview: true, NoResponseBehavior.Halt)
            .Should().Be(NoResponseOutcome.Halt);
    }

    [Theory]
    [InlineData(null, NoResponseBehavior.ContinueLastConfirmed)]
    [InlineData("", NoResponseBehavior.ContinueLastConfirmed)]
    [InlineData("   ", NoResponseBehavior.ContinueLastConfirmed)]
    [InlineData("bogus", NoResponseBehavior.ContinueLastConfirmed)]
    [InlineData("Halt", NoResponseBehavior.Halt)]
    [InlineData("halt", NoResponseBehavior.Halt)]
    [InlineData("ContinueLastConfirmed", NoResponseBehavior.ContinueLastConfirmed)]
    public void 設定の解釈は_未設定不正なら継続_fail_safe(string? value, NoResponseBehavior expected)
    {
        ReportNoResponsePolicy.ParseBehavior(value).Should().Be(expected);
    }
}
