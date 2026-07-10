using AiStockTrading.CostControl.Domain;
using AiStockTrading.CostControl.Worker.Foundation.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiStockTrading.CostControl.Worker.Tests;

// NFR（費用）, IADR-0027: 費用台帳 EF ストアの追記・月/カテゴリ別集計を InMemory DB で検証する。
public class EfCostLedgerTests
{
    private static CostControlDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<CostControlDbContext>().UseInMemoryDatabase(dbName).Options);

    [Fact]
    public void 月カテゴリ別に累計し別コンテキストからも読める()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
        {
            var ledger = new EfCostLedger(db);
            ledger.Record("2026-07", CostCategory.Llm, 1_000m, DateTimeOffset.UtcNow);
            ledger.Record("2026-07", CostCategory.Llm, 2_000m, DateTimeOffset.UtcNow);
            ledger.Record("2026-07", CostCategory.Infrastructure, 500m, DateTimeOffset.UtcNow);
            ledger.Record("2026-08", CostCategory.Llm, 9_000m, DateTimeOffset.UtcNow);
        }

        using var db2 = NewContext(dbName);
        var l = new EfCostLedger(db2);
        l.GetMonthlyTotal("2026-07", CostCategory.Llm).Should().Be(3_000m);
        l.GetMonthlyTotalAll("2026-07").Should().Be(3_500m);
        l.GetMonthlyTotal("2026-08", CostCategory.Llm).Should().Be(9_000m); // 別月
    }

    [Fact]
    public void 記録が無い月カテゴリは_0()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        new EfCostLedger(db).GetMonthlyTotal("2026-07", CostCategory.Llm).Should().Be(0m);
    }
}
