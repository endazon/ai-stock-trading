using AiStockTrading.CostControl.Domain;
using AiStockTrading.CostControl.Infrastructure.Foundation.Persistence;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiStockTrading.CostControl.Infrastructure.Tests;

// NFR（費用）, IADR-0055 決定5 / IADR-0034: 重複排除ストア（EF 実装）の性質を実 DbContext で検証する。
// フェイクでは検出できない「ChangeTracker を台帳と共有しない」ことが本質（claude-review 🔴 の回帰）。
public class EfProcessedMessageStoreTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    private static DbContextOptions<CostControlDbContext> NewOptions() =>
        new DbContextOptionsBuilder<CostControlDbContext>()
            .UseInMemoryDatabase($"processed-{Guid.NewGuid()}")
            .Options;

    [Fact]
    public void 初回は_true_二回目以降は_false_を返す()
    {
        var options = NewOptions();
        var store = new EfProcessedMessageStore(options);
        var id = Guid.NewGuid();

        store.TryMarkProcessed(id, At).Should().BeTrue();
        store.TryMarkProcessed(id, At).Should().BeFalse();
    }

    [Fact]
    public void Unmark_すると再び処理できる_計上失敗時の再試行()
    {
        var options = NewOptions();
        var store = new EfProcessedMessageStore(options);
        var id = Guid.NewGuid();

        store.TryMarkProcessed(id, At).Should().BeTrue();
        store.Unmark(id);

        store.TryMarkProcessed(id, At).Should().BeTrue();
    }

    // 🔴 回帰（claude-review 指摘）: 台帳（EfCostLedger）と DbContext を共有していると、計上の SaveChanges が
    // 失敗して CostEntryRow が Added のまま残った状態で Unmark の SaveChanges を呼んだ際に、
    // ロールバックされるべき計上行が一緒に INSERT されてしまう。本ストアは操作ごとに専用 DbContext を作るため
    // 台帳側の未確定変更を巻き込まない（IADR-0034「Record を他のユニットオブワークと組み合わせない」）。
    [Fact]
    public void 台帳の未確定変更を巻き込まない_ChangeTracker_を共有しない()
    {
        var options = NewOptions();
        var store = new EfProcessedMessageStore(options);
        var id = Guid.NewGuid();

        // 台帳側の DbContext（消費者スコープで共有され得るもの）に未確定の計上行がある状況を作る
        // ＝「Record の SaveChanges が失敗し Added のまま残留した」状態の模擬。
        using var ledgerDb = new CostControlDbContext(options);
        ledgerDb.CostEntries.Add(new CostEntryRow
        {
            Id = Guid.NewGuid(),
            Month = "2026-07",
            Category = CostCategory.Llm,
            Amount = 999m,
            RecordedAt = At,
        });

        store.TryMarkProcessed(id, At).Should().BeTrue();
        store.Unmark(id); // ここで台帳の未確定行が道連れで INSERT されてはならない

        using var verify = new CostControlDbContext(options);
        verify.CostEntries.Should().BeEmpty("重複排除ストアの SaveChanges が台帳の未確定な計上行を確定させてはならない");
        verify.ProcessedMessages.Should().BeEmpty("Unmark 済みのマークは残らない");
    }
}
