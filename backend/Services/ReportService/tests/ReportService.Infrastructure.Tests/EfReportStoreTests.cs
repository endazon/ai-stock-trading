using ReportService.Application;
using ReportService.Domain;
using ReportService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ReportService.Infrastructure.Tests;

// FR-06/07, IADR-0012/0024: 報告書 EF ストア（upsert・確定遷移・冪等・版排他・確定済み日報照会）を InMemory DB で検証する。
public class EfReportStoreTests
{
    private static ReportDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<ReportDbContext>().UseInMemoryDatabase(dbName).Options);

    private static TradingReport Daily(string key, DateOnly date, string policy = "方針") =>
        new() { PeriodKey = key, Kind = ReportKind.Daily, PeriodStart = date, PolicySummary = policy, AssumptionsVersion = 1 };

    [Fact]
    public void ドラフト作成と確定はラウンドトリップし別コンテキストからも読める()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
        {
            var store = new EfReportStore(db);
            store.UpsertDraft(Daily("daily-2026-07-10", new DateOnly(2026, 7, 10)), 0).Should().Be(1);
            store.Confirm("daily-2026-07-10", 1, DateTimeOffset.UtcNow)!.Transitioned.Should().BeTrue();
        }

        using var db2 = NewContext(dbName);
        var reloaded = new EfReportStore(db2).Get("daily-2026-07-10");
        reloaded!.Report.State.Should().Be(ReportState.Confirmed);
    }

    [Fact]
    public void 版番号が不一致の確定は競合で弾かれる()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfReportStore(db);
        store.UpsertDraft(Daily("daily-2026-07-10", new DateOnly(2026, 7, 10)), 0);

        var act = () => store.Confirm("daily-2026-07-10", expectedVersion: 99, DateTimeOffset.UtcNow);

        act.Should().Throw<ReportConcurrencyException>();
    }

    [Fact]
    public void 確定済みは冪等で再確定しても遷移しない()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfReportStore(db);
        store.UpsertDraft(Daily("daily-2026-07-10", new DateOnly(2026, 7, 10)), 0);
        store.Confirm("daily-2026-07-10", 1, DateTimeOffset.UtcNow);

        store.Confirm("daily-2026-07-10", 1, DateTimeOffset.UtcNow)!.Transitioned.Should().BeFalse();
    }

    // FR-07, IADR-0042/0071 決定5: レビュー局面（ReviewState）が別コンテキストへ永続し、提示・確定で正しく遷移する。
    [Fact]
    public void レビュー局面が永続し提示と確定で遷移する()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
        {
            var store = new EfReportStore(db);
            store.UpsertDraft(Daily("daily-2026-07-10", new DateOnly(2026, 7, 10)), 0);

            store.GetReview("daily-2026-07-10")!.State.Should().Be(ReviewState.Drafting);

            var present = store.ApplyReview("daily-2026-07-10", new ReviewCommand(ReviewAction.Present, "owner", 1));
            present!.Accepted.Should().BeTrue();
            present.Review.State.Should().Be(ReviewState.PendingApproval);
        }

        // 別コンテキストから局面が読める（永続確認）。
        using (var db2 = NewContext(dbName))
        {
            new EfReportStore(db2).GetReview("daily-2026-07-10")!.State.Should().Be(ReviewState.PendingApproval);
        }

        using var db3 = NewContext(dbName);
        var store3 = new EfReportStore(db3);
        store3.Confirm("daily-2026-07-10", 1, DateTimeOffset.UtcNow);
        store3.GetReview("daily-2026-07-10")!.State.Should().Be(ReviewState.Confirmed);
    }

    [Fact]
    public void 最新の確定済み日報を返す()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfReportStore(db);
        store.UpsertDraft(Daily("daily-2026-07-09", new DateOnly(2026, 7, 9), "9日"), 0);
        store.Confirm("daily-2026-07-09", 1, DateTimeOffset.UtcNow);
        store.UpsertDraft(Daily("daily-2026-07-10", new DateOnly(2026, 7, 10), "10日"), 0);
        store.Confirm("daily-2026-07-10", 1, DateTimeOffset.UtcNow);

        store.GetLatestConfirmed(ReportKind.Daily)!.Report.PolicySummary.Should().Be("10日");
    }
}
