using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ReportService.Common.Exceptions;
using ReportService.Domain;
using ReportService.Infrastructure.Persistence;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-07, #714, IADR-0024, IADR-0317, IADR-0319:
// **同一 PeriodKey の報告書ドラフトを 2 者が同時に作成する競合**を、順序を固定して再現する。
//
// 後から確定した側は一意キー違反で失敗するが、その例外型は**プロバイダごとに違う**
// （relational は DbUpdateException、EF Core の InMemory は ArgumentException）。
// 例外の型で競合を判定していると、取りこぼした側だけが素通りする（#707 の実測）。
//
// 時間に依存させると再現しないので、DbContext.SavingChanges を seam にして交錯を決定的に組み立てる。
public class EfReportStoreSeedRaceTests
{
    private const string PeriodKey = "daily-2026-09-09";

    private static ReportDbContext NewContext(string dbName, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<ReportDbContext>()
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(interceptors)
            .Options);

    private static TradingReport Daily(string policy) => new()
    {
        PeriodKey = PeriodKey,
        Kind = ReportKind.Daily,
        PeriodStart = new DateOnly(2026, 9, 9),
        PolicySummary = policy,
        AssumptionsVersion = 1,
    };

    // 後発が Add を stage した後・確定する前に、先発（別コンテキスト）へ 1 回だけ割り込ませる。
    private static Func<bool> InterleaveOnce(
        ReportDbContext late, string dbName, Action<ReportDbContext> early)
    {
        var fired = false;
        late.SavingChanges += (_, _) =>
        {
            if (fired)
            {
                return;
            }

            fired = true;
            using var earlyDb = NewContext(dbName);
            early(earlyDb);
        };

        return () => fired;
    }

    // 保存を必ず失敗させる（競合ではない障害の模擬）。行は 1 件も生まれない。
    private sealed class ThrowingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result) =>
            throw new DbUpdateException("保存に失敗した（競合ではない）。");
    }

    // 再現（是正前は赤）: 後発は競合（ReportConcurrencyException）として弾かれ、
    // **先発が確定させた実際の版**が例外に載る。
    [Fact]
    public void 同一期間の同時ドラフト作成は競合として弾かれる()
    {
        var dbName = Guid.NewGuid().ToString();
        using var late = NewContext(dbName);
        var interleaved = InterleaveOnce(late, dbName, early =>
            new EfReportStore(early).UpsertDraft(Daily("先発の方針"), expectedVersion: 0));

        var act = () => new EfReportStore(late).UpsertDraft(Daily("後発の方針"), expectedVersion: 0);

        act.Should().Throw<ReportConcurrencyException>();
        interleaved().Should().BeTrue("交錯が組み立てられていなければ本テストは何も検証していない");

        using var verify = NewContext(dbName);
        var stored = new EfReportStore(verify).Get(PeriodKey);
        stored!.Version.Should().Be(1, "先に確定させた側の行が残る");
        stored.Report.PolicySummary.Should().Be("先発の方針");
    }

    // 陽性対照: 競合が起きなければ従来どおり Version 1 でドラフトを作成できる。
    [Fact]
    public void 報告書は競合しなければドラフトを作成できる()
    {
        using var db = NewContext(Guid.NewGuid().ToString());

        new EfReportStore(db).UpsertDraft(Daily("方針"), expectedVersion: 0).Should().Be(1);
    }

    // 否定形: **競合ではない保存失敗を「版 0 との競合」に偽装しない。**
    // 従来は current?.Version ?? 0 として存在しない行との競合を発明していた。
    [Fact]
    public void 報告書は行が生まれない保存失敗を競合へ偽装せず送出する()
    {
        using var db = NewContext(Guid.NewGuid().ToString(), new ThrowingSaveChangesInterceptor());

        var act = () => new EfReportStore(db).UpsertDraft(Daily("方針"), expectedVersion: 0);

        act.Should().Throw<DbUpdateException>();
    }
}
