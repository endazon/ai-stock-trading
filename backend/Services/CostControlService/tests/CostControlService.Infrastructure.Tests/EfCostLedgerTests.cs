using AiStockTrading.CostControl.Application.Ports;
using AiStockTrading.CostControl.Domain;
using AiStockTrading.CostControl.Infrastructure.Foundation.Persistence;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiStockTrading.CostControl.Infrastructure.Tests;

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

    [Fact]
    public void Record_は_LLM累計の計上前後を原子的に返す()
    {
        // IADR-0034: しきい値遷移判定の入力（before/after）を計上と不可分に返す。
        var ledger = new EfCostLedger(NewContext(Guid.NewGuid().ToString()));

        ledger.Record("2026-07", CostCategory.Llm, 1_000m, DateTimeOffset.UtcNow)
            .Should().Be(new LlmCostRecordOutcome(0m, 1_000m));
        ledger.Record("2026-07", CostCategory.Llm, 2_000m, DateTimeOffset.UtcNow)
            .Should().Be(new LlmCostRecordOutcome(1_000m, 3_000m));
        // 非 LLM 計上は LLM 累計を変えない（before==after）。
        ledger.Record("2026-07", CostCategory.Infrastructure, 500m, DateTimeOffset.UtcNow)
            .Should().Be(new LlmCostRecordOutcome(3_000m, 3_000m));
    }

    // ---- カテゴリ別内訳（NFR（費用）, FR-16, 05_trading-assumptions §6.1, #347, IADR-0218/0219） ----

    // 🔴 **対象外（LlmUncapped）も返すことが本 API の存在理由である。**
    // §6.1 は対象外の費用について「抑制動作も行わず、**月報に実績を記載する**」と定める。
    // 対象内だけを返すと、#282 で実測された「報告書散文費用の過少申告」がそのまま再発する。
    [Fact]
    public void 月次内訳は上限対象外のカテゴリも含めて返す()
    {
        var ledger = new EfCostLedger(NewContext(Guid.NewGuid().ToString()));
        ledger.Record("2026-08", CostCategory.Llm, 1_000m, DateTimeOffset.UtcNow);
        ledger.Record("2026-08", CostCategory.Llm, 500m, DateTimeOffset.UtcNow);
        ledger.Record("2026-08", CostCategory.LlmUncapped, 300m, DateTimeOffset.UtcNow);
        ledger.Record("2026-08", CostCategory.Infrastructure, 20m, DateTimeOffset.UtcNow);

        var totals = ledger.GetMonthlyTotals("2026-08");

        totals[CostCategory.Llm].Should().Be(1_500m, "同カテゴリは合算される");
        totals[CostCategory.LlmUncapped].Should().Be(300m);
        totals[CostCategory.Infrastructure].Should().Be(20m);
        totals.Should().HaveCount(3, "計上の無いカテゴリは行を持たない");
    }

    // 月をまたいだ計上が混ざらないこと（月報は当月だけを載せる）。
    [Fact]
    public void 月次内訳は当月の計上だけを集計する()
    {
        var ledger = new EfCostLedger(NewContext(Guid.NewGuid().ToString()));
        ledger.Record("2026-07", CostCategory.Llm, 9_000m, DateTimeOffset.UtcNow);
        ledger.Record("2026-08", CostCategory.Llm, 100m, DateTimeOffset.UtcNow);
        ledger.Record("2026-08", CostCategory.LlmUncapped, 7m, DateTimeOffset.UtcNow);

        ledger.GetMonthlyTotals("2026-08").Should().BeEquivalentTo(new Dictionary<CostCategory, decimal>
        {
            [CostCategory.Llm] = 100m,
            [CostCategory.LlmUncapped] = 7m,
        });
    }

    // 🔴 計上が 1 件も無い月は**空**を返す（0 円の行を捏造しない）。
    // 「その月は使っていない」と「まだ計上経路が動いていない」を月報側で区別できるようにするため。
    [Fact]
    public void 計上の無い月の内訳は空になる()
    {
        var ledger = new EfCostLedger(NewContext(Guid.NewGuid().ToString()));

        ledger.GetMonthlyTotals("2026-09").Should().BeEmpty();
    }
}
