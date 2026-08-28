using AwesomeAssertions;
using Xunit;

namespace ReportService.Domain.Tests;

// FR-07, UC-03〜05, 07_discord-bot-design, IADR-0042: 対話的確定の状態遷移（承認/差し戻し/改訂・版番号付き冪等・OwnerOnly）を検証する。
public class ReportReviewStateMachineTests
{
    private const string Owner = "owner";

    private static ReportReview New(ReviewState state = ReviewState.Drafting, int version = 1) =>
        new("daily-2026-07-10", state, version);

    private static ReviewCommand Cmd(ReviewAction action, int expectedVersion = 1, string actor = Owner) =>
        new(action, actor, expectedVersion);

    // --- 正常系（07_discord-bot-design のシーケンス） ---

    [Fact]
    public void 提示から承認までの正常系で確定し版が上がる()
    {
        var review = New(ReviewState.Drafting, version: 1);

        var presented = ReportReviewStateMachine.Decide(review, Cmd(ReviewAction.Present, 1));
        presented.Accepted.Should().BeTrue();
        presented.Transitioned.Should().BeTrue();
        presented.Review.State.Should().Be(ReviewState.PendingApproval);
        presented.Review.Version.Should().Be(1); // 提示は版を上げない（内容不変）

        var approved = ReportReviewStateMachine.Decide(presented.Review, Cmd(ReviewAction.Approve, 1));
        approved.Transitioned.Should().BeTrue();
        approved.Review.State.Should().Be(ReviewState.Confirmed);
        approved.Review.Version.Should().Be(2); // 確定は版+1（IADR-0024 踏襲）
    }

    [Fact]
    public void 差し戻しと改訂を経て再提示から確定できる()
    {
        var review = New(ReviewState.PendingApproval, version: 1);

        // 差し戻し（修正指示）→ ChangesRequested（版は不変）
        var rejected = ReportReviewStateMachine.Decide(review, Cmd(ReviewAction.RequestChanges, 1));
        rejected.Transitioned.Should().BeTrue();
        rejected.Review.State.Should().Be(ReviewState.ChangesRequested);
        rejected.Review.Version.Should().Be(1);

        // 改訂（新ドラフト）→ Drafting（版+1）
        var revised = ReportReviewStateMachine.Decide(rejected.Review, Cmd(ReviewAction.Revise, 1));
        revised.Review.State.Should().Be(ReviewState.Drafting);
        revised.Review.Version.Should().Be(2);

        // 再提示 → PendingApproval（版2で承認）
        var represented = ReportReviewStateMachine.Decide(revised.Review, Cmd(ReviewAction.Present, 2));
        represented.Review.State.Should().Be(ReviewState.PendingApproval);

        var approved = ReportReviewStateMachine.Decide(represented.Review, Cmd(ReviewAction.Approve, 2));
        approved.Review.State.Should().Be(ReviewState.Confirmed);
        approved.Review.Version.Should().Be(3);
    }

    [Fact]
    public void 承認待ちからの改訂は版を上げてドラフトに戻る()
    {
        var review = New(ReviewState.PendingApproval, version: 2);

        var revised = ReportReviewStateMachine.Decide(review, Cmd(ReviewAction.Revise, 2));

        revised.Transitioned.Should().BeTrue();
        revised.Review.State.Should().Be(ReviewState.Drafting);
        revised.Review.Version.Should().Be(3);
    }

    // --- 冪等 ---

    [Fact]
    public void 確定済みへの再承認は版非依存で冪等()
    {
        var review = New(ReviewState.Confirmed, version: 3);

        // 利用者が見た確定前の版（2）で再確定要求が来ても冪等に成功する（二重実行防止・IADR-0024 踏襲）。
        var again = ReportReviewStateMachine.Decide(review, Cmd(ReviewAction.Approve, expectedVersion: 2));

        again.Accepted.Should().BeTrue();
        again.Transitioned.Should().BeFalse();
        again.Review.Should().Be(review); // 状態・版とも不変
    }

    [Fact]
    public void 提示済みへの再提示は冪等()
    {
        var review = New(ReviewState.PendingApproval, version: 1);

        var again = ReportReviewStateMachine.Decide(review, Cmd(ReviewAction.Present, 1));

        again.Accepted.Should().BeTrue();
        again.Transitioned.Should().BeFalse();
        again.Review.Should().Be(review);
    }

    [Fact]
    public void 差し戻し済みへの再差し戻しは冪等()
    {
        var review = New(ReviewState.ChangesRequested, version: 1);

        var again = ReportReviewStateMachine.Decide(review, Cmd(ReviewAction.RequestChanges, 1));

        again.Transitioned.Should().BeFalse();
        again.Accepted.Should().BeTrue();
    }

    // --- ガード（拒否・状態不変） ---

    [Fact]
    public void 操作者が空なら拒否し状態は変わらない()
    {
        var review = New(ReviewState.PendingApproval, version: 1);

        var decision = ReportReviewStateMachine.Decide(review, Cmd(ReviewAction.Approve, 1, actor: "  "));

        decision.Accepted.Should().BeFalse();
        decision.Rejection.Should().Be(ReviewRejectionReason.ActorRequired);
        decision.Review.Should().Be(review);
    }

    [Fact]
    public void 古い版での状態変更は版競合で拒否される()
    {
        var review = New(ReviewState.PendingApproval, version: 5);

        var decision = ReportReviewStateMachine.Decide(review, Cmd(ReviewAction.Approve, expectedVersion: 4));

        decision.Rejection.Should().Be(ReviewRejectionReason.VersionConflict);
        decision.Review.Should().Be(review);
    }

    [Theory]
    [InlineData(ReviewAction.Present)]
    [InlineData(ReviewAction.RequestChanges)]
    [InlineData(ReviewAction.Revise)]
    public void 確定済みからの改訂系は不変で拒否される(ReviewAction action)
    {
        var review = New(ReviewState.Confirmed, version: 3);

        var decision = ReportReviewStateMachine.Decide(review, Cmd(action, 3));

        decision.Rejection.Should().Be(ReviewRejectionReason.AlreadyConfirmed);
        decision.Review.Should().Be(review);
    }

    [Theory]
    [InlineData(ReviewState.Drafting, ReviewAction.Approve)]
    [InlineData(ReviewState.Drafting, ReviewAction.RequestChanges)]
    [InlineData(ReviewState.Drafting, ReviewAction.Revise)]
    [InlineData(ReviewState.ChangesRequested, ReviewAction.Approve)]
    public void 許されない遷移は不正遷移で拒否される(ReviewState state, ReviewAction action)
    {
        var review = New(state, version: 1);

        var decision = ReportReviewStateMachine.Decide(review, Cmd(action, 1));

        decision.Rejection.Should().Be(ReviewRejectionReason.InvalidTransition);
        decision.Review.Should().Be(review);
    }

    [Fact]
    public void 操作者チェックは版や遷移チェックより優先される()
    {
        // Actor 空なら、版不一致や不正遷移よりも先に ActorRequired を返す（決定的な優先順位）。
        var review = New(ReviewState.Drafting, version: 1);

        var decision = ReportReviewStateMachine.Decide(review, Cmd(ReviewAction.Approve, expectedVersion: 99, actor: ""));

        decision.Rejection.Should().Be(ReviewRejectionReason.ActorRequired);
    }
}
