using System.Text.Json;
using AuditService.Features.AuditEvents;
using AuditService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AuditService.Tests;

// FR-11, UC-07, ADR-0016 決定15, #339, IADR-0226:
// **「後から集計可能な粒度で記録する」ことの検証。**
//
// ADR-0016 決定15 は集計機能（FR-18）を対象外としたまま
// 「**集計は後から作れても記録は遡って復元できない**」という理由で記録の設計を求めた。
// したがって表明すべきは「集計が動くこと」ではなく
// **「台帳に残した内容だけから建玉別・区分別の集計を再構成できること」**である。
//
// 🔴 **経費台帳の実体は監査台帳である**（専用テーブルを作らない。IADR-0226 決定4）。
// ここでは実際に監査台帳へ記録し、**その `Detail`（イベント全量 JSON）を読み戻して**集計する ——
// 型を直接畳むテストでは「記録に必要な情報が載っているか」を一切確かめられない。
public class TradeExpenseAuditProjectionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 8, 28);

    private static TradeExpenseRecorded Expense(
        string symbol, Market market, TradeExpenseCategory category, decimal amountUsd, string sourceId) =>
        new(new TradeExpense(symbol, market, category, amountUsd, Day, sourceId, T0));

    // 監査台帳へ記録 → Detail を読み戻す → 集計する、の一連。
    private static IReadOnlyList<PositionExpenseSummary> RecordAndProject(
        params TradeExpenseRecorded[] events)
    {
        var store = new InMemoryAuditEventStore();
        foreach (var e in events)
        {
            store.Append(AuditEntryFactory.From(e, Guid.NewGuid(), T0));
        }

        var restored = store.GetRecent(1_000)
            .Where(entry => entry.EventType == nameof(TradeExpenseRecorded))
            .Select(entry => JsonSerializer.Deserialize<TradeExpenseRecorded>(entry.Detail, AuditDetailJson.Options)!)
            .Select(e => e.Expense)
            .ToList();

        return TradeExpenseLedger.SummarizeByPosition(restored);
    }

    [Fact]
    public void 台帳へ残した内容だけから建玉別_区分別の集計を再構成できる()
    {
        var summaries = RecordAndProject(
            Expense("AAPL", Market.UnitedStates, TradeExpenseCategory.Commission, 1.00m, "ORD-1"),
            Expense("AAPL", Market.UnitedStates, TradeExpenseCategory.Commission, 2.00m, "ORD-2"),
            Expense("AAPL", Market.UnitedStates, TradeExpenseCategory.BorrowFee, 0.50m, "ACC-1"),
            Expense("MSFT", Market.UnitedStates, TradeExpenseCategory.FxCost, 0.25m, "FX-1"));

        summaries.Select(s => s.Symbol).Should().Equal("AAPL", "MSFT");

        var apple = summaries.Single(s => s.Symbol == "AAPL");
        apple.For(TradeExpenseCategory.Commission).AmountUsd.Should().Be(3.00m);
        apple.For(TradeExpenseCategory.Commission).LineCount.Should().Be(2);
        apple.For(TradeExpenseCategory.BorrowFee).AmountUsd.Should().Be(0.50m);
        apple.TotalExpensesUsd.Should().Be(3.50m);

        // 未計上の区分は 0 件として残る（0 円と区別できる）。
        apple.For(TradeExpenseCategory.MarginInterest).HasLines.Should().BeFalse();
    }

    // 🔴 ADR-0016 決定15 の要点。**記録を読み戻した後も**配当相当額が実現損益へ流れ込まない。
    [Fact]
    public void 否定形_読み戻した配当相当額は実現損益に混ざらない()
    {
        var summaries = RecordAndProject(
            Expense("AAPL", Market.UnitedStates, TradeExpenseCategory.Realized, 100.00m, "R-1"),
            Expense("AAPL", Market.UnitedStates, TradeExpenseCategory.DividendInLieu, 2.50m, "DIV-1"));

        var apple = summaries.Should().ContainSingle().Subject;
        apple.RealizedUsd.Should().Be(100.00m);
        apple.For(TradeExpenseCategory.DividendInLieu).AmountUsd.Should().Be(2.50m);
        apple.TotalExpensesUsd.Should().Be(2.50m, "配当相当額は費用側に入り、実現損益は費用合計に入らない");
    }

    // 区分そのものが台帳の JSON に残っていなければ、後から区分別に分けることはできない。
    // 🔴 **列挙は文字列で残る**（人が台帳を読んで監査できるようにするため）。
    [Fact]
    public void 区分は台帳の_JSON_に文字列として残る()
    {
        var store = new InMemoryAuditEventStore();
        var e = Expense("AAPL", Market.UnitedStates, TradeExpenseCategory.DividendInLieu, 2.50m, "DIV-1");
        store.Append(AuditEntryFactory.From(e, Guid.NewGuid(), T0));

        var detail = store.GetRecent(1).Should().ContainSingle().Subject.Detail;

        detail.Should().Contain(nameof(TradeExpenseCategory.DividendInLieu));
        detail.Should().NotContain("\"Category\":3", "列挙は序数ではなく文字列で残す（人が読める形）");
    }

    // 建玉 1 件ぶんの照会も、台帳に残した (銘柄, 市場) の組だけで成立する。
    [Fact]
    public void 建玉_1_件の集計は市場まで含めて絞り込める()
    {
        var store = new InMemoryAuditEventStore();
        foreach (var e in new[]
        {
            Expense("0001", Market.UnitedStates, TradeExpenseCategory.Fee, 1.00m, "US-1"),
            Expense("0001", Market.Japan, TradeExpenseCategory.Fee, 5.00m, "JP-1"),
        })
        {
            store.Append(AuditEntryFactory.From(e, Guid.NewGuid(), T0));
        }

        var restored = store.GetRecent(1_000)
            .Select(entry => JsonSerializer.Deserialize<TradeExpenseRecorded>(entry.Detail, AuditDetailJson.Options)!)
            .Select(e => e.Expense)
            .ToList();

        TradeExpenseLedger.SummarizePosition(restored, "0001", Market.Japan)
            .For(TradeExpenseCategory.Fee).AmountUsd.Should().Be(5.00m);
        TradeExpenseLedger.SummarizePosition(restored, "0001", Market.UnitedStates)
            .For(TradeExpenseCategory.Fee).AmountUsd.Should().Be(1.00m);
    }
}
