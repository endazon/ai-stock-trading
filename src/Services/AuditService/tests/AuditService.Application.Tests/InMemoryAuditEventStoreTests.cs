using AiStockTrading.Audit.Application.Adapters;
using AiStockTrading.Audit.Application.State;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.Audit.Application.Tests;

// FR-11, IADR-0019: 監査台帳（インメモリ）の追記・冪等・相関/期間照会を検証する。
public class InMemoryAuditEventStoreTests
{
    private static AuditEntry Entry(Guid id, Guid correlationId, DateTimeOffset occurredAt, string type = "OrderApproved") =>
        new(id, type, correlationId, "AAPL", "要約", "{}", occurredAt, DateTimeOffset.UtcNow);

    [Fact]
    public void 相関IDで記録を時系列昇順に返す()
    {
        var store = new InMemoryAuditEventStore();
        var corr = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);
        store.Append(Entry(Guid.NewGuid(), corr, t0.AddMinutes(2), "OrderExecuted"));
        store.Append(Entry(Guid.NewGuid(), corr, t0, "TradeDecisionMade"));
        store.Append(Entry(Guid.NewGuid(), corr, t0.AddMinutes(1), "OrderApproved"));
        store.Append(Entry(Guid.NewGuid(), Guid.NewGuid(), t0)); // 別相関

        var result = store.GetByCorrelation(corr);

        result.Should().HaveCount(3);
        result.Select(e => e.EventType).Should().ContainInOrder("TradeDecisionMade", "OrderApproved", "OrderExecuted");
    }

    [Fact]
    public void 同一_Id_の再送は重複記録しない()
    {
        var store = new InMemoryAuditEventStore();
        var id = Guid.NewGuid();
        var corr = Guid.NewGuid();
        store.Append(Entry(id, corr, DateTimeOffset.UtcNow));
        store.Append(Entry(id, corr, DateTimeOffset.UtcNow)); // 再送

        store.GetByCorrelation(corr).Should().HaveCount(1);
    }

    [Fact]
    public void 直近は降順で_limit_件に制限される()
    {
        var store = new InMemoryAuditEventStore();
        var t0 = new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 5; i++)
            store.Append(Entry(Guid.NewGuid(), Guid.NewGuid(), t0.AddMinutes(i)));

        var recent = store.GetRecent(3);

        recent.Should().HaveCount(3);
        recent[0].OccurredAt.Should().Be(t0.AddMinutes(4)); // 最新が先頭
    }
}
