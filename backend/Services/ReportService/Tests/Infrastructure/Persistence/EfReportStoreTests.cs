using ReportService.Common.Exceptions;
using ReportService.Domain;
using ReportService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ReportService.Tests;

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

    // FR-15, FR-07, ADR-0033 決定2, #1308, IADR-0528 決定3: ある時点で確定していた日報のうち最新を返す。
    // EF の実装と InMemory の実装に同じ場合を当てる（両方とも本番の構成で使われ得る）。
    public static TheoryData<string> Stores() => ["ef", "in-memory"];

    private static ReportService.Features.Reports.IReportStore NewStore(string kind) =>
        kind == "ef" ? new EfReportStore(NewContext(Guid.NewGuid().ToString())) : new InMemoryReportStore();

    private static readonly DateTimeOffset NineConfirmed = new(2026, 9, 15, 23, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset TenConfirmed = new(2026, 9, 16, 23, 0, 0, TimeSpan.Zero);

    private static ReportService.Features.Reports.IReportStore SeedTwoDays(string kind)
    {
        var store = NewStore(kind);
        store.UpsertDraft(Daily("daily-2026-09-15", new DateOnly(2026, 9, 15), "15日"), 0);
        store.Confirm("daily-2026-09-15", 1, NineConfirmed);
        store.UpsertDraft(Daily("daily-2026-09-16", new DateOnly(2026, 9, 16), "16日"), 0);
        store.Confirm("daily-2026-09-16", 1, TenConfirmed);
        // 期間の始まりは最も新しいが、確定していない（ドラフト）日報。どの時点でも返さない。
        store.UpsertDraft(Daily("daily-2026-09-17", new DateOnly(2026, 9, 17), "17日（ドラフト）"), 0);
        return store;
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void 時点で確定していた最新の日報を返し_後で確定した日報とドラフトは返さない(string kind)
    {
        var store = SeedTwoDays(kind);

        // 16 日の日報が確定する 1 秒前: 15 日の日報が当時の答え（16 日の日報は期間が新しくても、まだ確定していない）。
        store.GetLatestConfirmedAsOf(ReportKind.Daily, TenConfirmed.AddSeconds(-1))!.Report.PolicySummary.Should().Be("15日");
        // 確定のちょうどその時刻は含む（ConfirmedAt ≤ at）。
        store.GetLatestConfirmedAsOf(ReportKind.Daily, TenConfirmed)!.Report.PolicySummary.Should().Be("16日");
        // 後の時刻でもドラフトは返さない。
        store.GetLatestConfirmedAsOf(ReportKind.Daily, TenConfirmed.AddDays(10))!.Report.PolicySummary.Should().Be("16日");
        // 最初の確定より前は無い。
        store.GetLatestConfirmedAsOf(ReportKind.Daily, NineConfirmed.AddSeconds(-1)).Should().BeNull();
        // 種別が違えば無い。
        store.GetLatestConfirmedAsOf(ReportKind.Weekly, TenConfirmed.AddDays(10)).Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void 時刻のオフセットが違っても同じ瞬間なら同じ答えになる(string kind)
    {
        var store = SeedTwoDays(kind);
        // 2026-09-16 19:00 −04:00（ET の夏時間）＝ 23:00Z。16 日の日報が確定した瞬間と同じ。
        var sameInstant = new DateTimeOffset(2026, 9, 16, 19, 0, 0, TimeSpan.FromHours(-4));

        store.GetLatestConfirmedAsOf(ReportKind.Daily, sameInstant)!.Report.PolicySummary.Should().Be("16日");
        store.GetLatestConfirmedAsOf(ReportKind.Daily, sameInstant.AddTicks(-1))!.Report.PolicySummary.Should().Be("15日");
    }
}
