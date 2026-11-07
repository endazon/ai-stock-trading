using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using NotificationService.Infrastructure.Steps;
using Xunit;

namespace NotificationService.Tests;

// NFR-06, FR-14, FR-09, IADR-0626 決定 3: 確認コードの成功・失敗・停止はオーナーへ知らせ、止めている間の試行は知らせない
// （連打で通知を溢れさせない。監査には全件が残る）。
public class OwnerSecondFactorCheckedNotificationHandlerTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 7, 3, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Verified", false, 1)]
    [InlineData("Invalid", false, 1)]
    [InlineData("Replayed", false, 1)]
    [InlineData("Invalid", true, 1)]
    [InlineData("LockedOut", false, 0)]
    public async Task 成功と失敗と停止を知らせ_止めている間の試行は知らせない(string outcome, bool lockStarted, int expected)
    {
        var sender = new RecordingNotificationSender();
        var handler = new OwnerSecondFactorCheckedNotificationHandler(sender);

        await handler.Handle(
            new OwnerSecondFactorChecked("KillSwitchDisengage", "kill-switch", outcome, 0, lockStarted ? 1 : 0,
                lockStarted ? T.AddMinutes(15) : null, false, lockStarted, T),
            CancellationToken.None);

        sender.Sent.Should().HaveCount(expected);
    }

    // IADR-0626 決定 3（2026-11-07 追記）: 状態を読み書きできないことは、続いた 1 回目だけ知らせる。
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    [InlineData(10, 0)]
    public async Task 確かめられない状態は続いた1回目だけ知らせる(int streak, int expected)
    {
        var sender = new RecordingNotificationSender();

        await new OwnerSecondFactorCheckedNotificationHandler(sender).Handle(
            new OwnerSecondFactorChecked("StagePromote", "stage:Stage3", "Unavailable", 0, 0, null, false, false, T, streak),
            CancellationToken.None);

        sender.Sent.Should().HaveCount(expected);
    }
}
