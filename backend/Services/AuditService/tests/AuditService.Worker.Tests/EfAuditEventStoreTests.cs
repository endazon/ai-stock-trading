using AiStockTrading.Audit.Application.State;
using AiStockTrading.Audit.Worker.Foundation.Persistence;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiStockTrading.Audit.Worker.Tests;

// FR-11, IADR-0019: 監査台帳 EF ストアの追記・冪等・相関/期間照会を InMemory DB で検証する。
public class EfAuditEventStoreTests
{
    private static AuditDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    private static AuditEntry Entry(Guid id, Guid correlationId, DateTimeOffset occurredAt, string type = "OrderApproved") =>
        new(id, type, correlationId, "AAPL", "要約", "{}", occurredAt, DateTimeOffset.UtcNow);

    [Fact]
    public void 追記した記録は相関で時系列昇順に返り別コンテキストからも読める()
    {
        var dbName = Guid.NewGuid().ToString();
        var corr = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);

        using (var db = NewContext(dbName))
        {
            var store = new EfAuditEventStore(db);
            store.Append(Entry(Guid.NewGuid(), corr, t0.AddMinutes(1), "OrderApproved"));
            store.Append(Entry(Guid.NewGuid(), corr, t0, "TradeDecisionMade"));
        }

        using var db2 = NewContext(dbName);
        var result = new EfAuditEventStore(db2).GetByCorrelation(corr);
        result.Select(e => e.EventType).Should().ContainInOrder("TradeDecisionMade", "OrderApproved");
    }

    [Fact]
    public void 同一_Id_の再送は重複記録しない()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfAuditEventStore(db);
        var id = Guid.NewGuid();
        var corr = Guid.NewGuid();

        store.Append(Entry(id, corr, DateTimeOffset.UtcNow));
        store.Append(Entry(id, corr, DateTimeOffset.UtcNow)); // 再送

        store.GetByCorrelation(corr).Should().HaveCount(1);
    }

    [Fact]
    public void 直近は降順で_limit_件に制限される()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfAuditEventStore(db);
        var t0 = new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 5; i++)
            store.Append(Entry(Guid.NewGuid(), Guid.NewGuid(), t0.AddMinutes(i)));

        var recent = store.GetRecent(2);

        recent.Should().HaveCount(2);
        recent[0].OccurredAt.Should().Be(t0.AddMinutes(4));
    }
}
