using AiStockTrading.Report.Application;
using AiStockTrading.Report.Domain;
using AiStockTrading.Report.Worker.Foundation.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiStockTrading.Report.Worker.Tests;

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
