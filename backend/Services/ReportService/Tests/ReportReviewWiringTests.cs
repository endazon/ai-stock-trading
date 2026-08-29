using ReportService.Infrastructure.Persistence;
using ReportService.Common.Abstractions;
using ReportService.Features.Reports;
using ReportService.Domain;
using AwesomeAssertions;
using Xunit;
using AppSvc = ReportService.Features.Reports.ReportAppService;

namespace ReportService.Tests;

// FR-07, UC-03〜05, IADR-0042/0071 決定5: ReportReviewStateMachine を永続ストアへ結線した対話的確定を検証する
// （提示・差し戻しの永続化・版番号・確定連携）。
public class ReportReviewWiringTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 6, 0, 0, TimeSpan.Zero);
    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => Now; }

    private static (AppSvc Svc, IReportStore Store) NewService()
    {
        var store = new InMemoryReportStore();
        return (new AppSvc(store, new FixedClock()), store);
    }

    private static TradingReport Daily() => new()
    {
        PeriodKey = "daily-2026-07-10",
        Kind = ReportKind.Daily,
        PeriodStart = new DateOnly(2026, 7, 10),
        PolicySummary = "翌営業日は押し目買い",
    };

    [Fact]
    public void 新規ドラフトのレビュー局面は_Drafting()
    {
        var (svc, _) = NewService();
        svc.UpsertDraft(Daily(), 0);

        var review = svc.GetReview("daily-2026-07-10");
        review!.State.Should().Be(ReviewState.Drafting);
        review.Version.Should().Be(1);
    }

    [Fact]
    public void 提示で_PendingApproval_へ遷移し版番号は不変()
    {
        var (svc, _) = NewService();
        svc.UpsertDraft(Daily(), 0);

        var decision = svc.ApplyReview("daily-2026-07-10", ReviewAction.Present, expectedVersion: 1, actor: "owner");

        decision!.Accepted.Should().BeTrue();
        decision.Review.State.Should().Be(ReviewState.PendingApproval);
        svc.GetReview("daily-2026-07-10")!.Version.Should().Be(1); // 内容不変のため版は変わらない
    }

    [Fact]
    public void 提示後の差し戻しで_ChangesRequested_へ()
    {
        var (svc, _) = NewService();
        svc.UpsertDraft(Daily(), 0);
        svc.ApplyReview("daily-2026-07-10", ReviewAction.Present, 1, "owner");

        var decision = svc.ApplyReview("daily-2026-07-10", ReviewAction.RequestChanges, 1, "owner");

        decision!.Accepted.Should().BeTrue();
        svc.GetReview("daily-2026-07-10")!.State.Should().Be(ReviewState.ChangesRequested);
    }

    [Fact]
    public void 版番号不一致の提示は拒否_状態不変()
    {
        var (svc, _) = NewService();
        svc.UpsertDraft(Daily(), 0);

        var decision = svc.ApplyReview("daily-2026-07-10", ReviewAction.Present, expectedVersion: 99, actor: "owner");

        decision!.Accepted.Should().BeFalse();
        decision.Rejection.Should().Be(ReviewRejectionReason.VersionConflict);
        svc.GetReview("daily-2026-07-10")!.State.Should().Be(ReviewState.Drafting); // 不変
    }

    [Fact]
    public void 改訂_再upsert_はレビュー局面を_Drafting_へ戻す()
    {
        var (svc, _) = NewService();
        svc.UpsertDraft(Daily(), 0);
        svc.ApplyReview("daily-2026-07-10", ReviewAction.Present, 1, "owner"); // PendingApproval

        // 改訂＝新ドラフト（版+1）。レビュー局面は Drafting へ戻る。
        var v = svc.UpsertDraft(Daily() with { PolicySummary = "改訂案" }, 1);

        v.Should().Be(2);
        svc.GetReview("daily-2026-07-10")!.State.Should().Be(ReviewState.Drafting);
    }

    [Fact]
    public void 確定でレビュー局面は_Confirmed_確定後の提示は拒否()
    {
        var (svc, _) = NewService();
        svc.UpsertDraft(Daily(), 0);
        svc.ApplyReview("daily-2026-07-10", ReviewAction.Present, 1, "owner");
        svc.Confirm("daily-2026-07-10", 1, "owner");

        svc.GetReview("daily-2026-07-10")!.State.Should().Be(ReviewState.Confirmed);

        var decision = svc.ApplyReview("daily-2026-07-10", ReviewAction.Present, 2, "owner");
        decision!.Rejection.Should().Be(ReviewRejectionReason.AlreadyConfirmed);
    }

    [Fact]
    public void 存在しない報告書のレビュー操作は_null()
    {
        var (svc, _) = NewService();
        svc.ApplyReview("daily-nope", ReviewAction.Present, 1, "owner").Should().BeNull();
        svc.GetReview("daily-nope").Should().BeNull();
    }

    [Fact]
    public void レビュー操作にはアクターが必須()
    {
        var (svc, _) = NewService();
        svc.UpsertDraft(Daily(), 0);

        var act = () => svc.ApplyReview("daily-2026-07-10", ReviewAction.Present, 1, actor: "");
        act.Should().Throw<ArgumentException>();
    }
}
