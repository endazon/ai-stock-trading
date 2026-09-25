using AwesomeAssertions;
using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationService.Features.Notifications.RevisePolicy;
using NotificationService.Infrastructure.ExternalServices;
using Xunit;

namespace NotificationService.Tests;

// FR-07, FR-14, ADR-0003, #1016, IADR-0431 決定 5: `/policy` の応答の送り順と確認ボタンの位置（T-10-1348〜1351。再監査 nit 2）。
// DiscordNetBotGateway.OnPolicySlashAsync はこの送信器へ `FollowupTextAsync` を渡すだけである。
public class PolicyRevisionReplySenderTests
{
    private static readonly PolicyRevisionCommandResult Proposed = PolicyRevisionCommandResult.Proposed(
        ["見出し", "【方針案 1/2】\n前半", "【方針案 2/2】\n後半", "【監視銘柄の入れ替え案】…"], "daily-2026-09-27", 3);

    private sealed class Recorder(int? failAt = null, bool failNotice = false)
    {
        public List<(string Text, MessageComponent? Components)> Sent { get; } = [];

        private bool _failed;

        // failAt 番目（0 起点）の送信を 1 回だけ失敗させる。failNotice なら失敗の後の送信（知らせ）も失敗させる。
        public Task Followup(string text, MessageComponent? components)
        {
            if ((!_failed && failAt == Sent.Count) || (_failed && failNotice))
            {
                _failed = true;
                throw new HttpRequestException("Discord が応答しない（模擬）");
            }
            Sent.Add((text, components));
            return Task.CompletedTask;
        }
    }

    private static IEnumerable<string> ButtonIds(MessageComponent? components) =>
        components?.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>()
            .Select(b => b.CustomId) ?? [];

    // T-10-1348: 通は順に送られ、確認ボタンは最後の通にだけ付く（版を運ぶ）。
    [Fact]
    public async Task 確認ボタンは最後の通にだけ付く()
    {
        var recorder = new Recorder();

        await PolicyRevisionReplySender.SendAsync(Proposed, recorder.Followup, NullLogger.Instance);

        recorder.Sent.Select(s => s.Text).Should().Equal(
            "見出し", "【方針案 1/2】\n前半", "【方針案 2/2】\n後半", "【監視銘柄の入れ替え案】…" + PolicyRevisionReplySender.ApprovePrompt);
        recorder.Sent.Take(3).Should().OnlyContain(s => !ButtonIds(s.Components).Any());
        ButtonIds(recorder.Sent[^1].Components).Should().Equal("ast-report-approve-daily-2026-09-27-3");
    }

    // T-10-1349: 途中（方針の 2 通目・ボタンの通）で送れなければボタンは出ず、版が承認待ちで保存されていること・確定と
    // やり直しの方法を知らせる。
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task 途中で送れなければボタンを出さず承認待ちの版を知らせる(int failAt)
    {
        var recorder = new Recorder(failAt);

        await PolicyRevisionReplySender.SendAsync(Proposed, recorder.Followup, NullLogger.Instance);

        recorder.Sent.Should().OnlyContain(s => !ButtonIds(s.Components).Any(), "全文が届いていないのでボタンを出さない");
        var notice = recorder.Sent[^1].Text;
        notice.Should().Contain("版 3").And.Contain("承認待ち").And.Contain("まだ確定していません")
            .And.Contain("/report approve period:daily-2026-09-27").And.Contain("/policy");
    }

    // T-10-1350: 知らせも送れなくても例外を Discord.Net へ返さない（Bot を落とさない）。
    [Fact]
    public async Task 知らせも送れなくても例外を返さない()
    {
        var recorder = new Recorder(failAt: 1, failNotice: true);

        var act = () => PolicyRevisionReplySender.SendAsync(Proposed, recorder.Followup, NullLogger.Instance);

        await act.Should().NotThrowAsync();
        recorder.Sent.Should().ContainSingle().Which.Text.Should().Be("見出し");
    }

    // T-10-1351: 未提示の案・失敗・拒否ではボタンを出さない（拒否は理由を出さず一般化する）。
    [Fact]
    public async Task 未提示と失敗と拒否ではボタンを出さない()
    {
        var unpresented = new Recorder();
        await PolicyRevisionReplySender.SendAsync(
            PolicyRevisionCommandResult.Proposed(["見出し", "【方針案 1/1】\n方針", "末尾"], null, null), unpresented.Followup, NullLogger.Instance);
        unpresented.Sent.Select(s => s.Text).Should().Equal("見出し", "【方針案 1/1】\n方針", "末尾");
        unpresented.Sent.Should().OnlyContain(s => !ButtonIds(s.Components).Any());

        var failed = new Recorder();
        await PolicyRevisionReplySender.SendAsync(PolicyRevisionCommandResult.Failed("AI の案を作れませんでした"), failed.Followup, NullLogger.Instance);
        failed.Sent.Should().ContainSingle().Which.Text.Should().Be("AI の案を作れませんでした");

        var denied = new Recorder();
        await PolicyRevisionReplySender.SendAsync(PolicyRevisionCommandResult.Denied("許可リスト外"), denied.Followup, NullLogger.Instance);
        denied.Sent.Should().ContainSingle().Which.Text.Should().Be("この操作は実行されませんでした（許可されていません）。");
    }
}
