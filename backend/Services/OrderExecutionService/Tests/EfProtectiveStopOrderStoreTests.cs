using OrderExecutionService.Domain;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-10, #331, IADR-0210 決定6: 保護逆指値レグ記録の永続化を InMemory DB で検証する
// （契約: EntryDecisionId upsert・Active の洗い出し・ラウンドトリップ）。
public class EfProtectiveStopOrderStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 6, 0, 0, TimeSpan.Zero);

    private static OrderExecutionDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<OrderExecutionDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    private static ProtectiveStopOrder Stop(Guid entryDecisionId, int attempt = 1,
        ProtectiveStopState state = ProtectiveStopState.Active, DateTimeOffset? createdAt = null) =>
        new(entryDecisionId, ProtectiveStopIds.StopDecisionId(entryDecisionId, attempt), $"stop-{attempt}",
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
            10, 950m, 1m, attempt, state, createdAt ?? Now, Now);

    [Fact]
    public void 保存はラウンドトリップし別コンテキストからも読める()
    {
        var dbName = Guid.NewGuid().ToString();
        var entryDecisionId = Guid.NewGuid();
        var stop = Stop(entryDecisionId);

        using (var db = NewContext(dbName))
        {
            new EfProtectiveStopOrderStore(db).Save(stop);
        }

        using var db2 = NewContext(dbName);
        var found = new EfProtectiveStopOrderStore(db2).Find(entryDecisionId);
        found.Should().Be(stop);
    }

    [Fact]
    public void 同一エントリーへの再保存は上書きになる_再発注の試行置き換え()
    {
        var dbName = Guid.NewGuid().ToString();
        var entryDecisionId = Guid.NewGuid();

        using (var db = NewContext(dbName))
        {
            var store = new EfProtectiveStopOrderStore(db);
            store.Save(Stop(entryDecisionId, attempt: 1));
            store.Save(Stop(entryDecisionId, attempt: 2));
        }

        using var db2 = NewContext(dbName);
        var found = new EfProtectiveStopOrderStore(db2).Find(entryDecisionId);
        found!.Attempt.Should().Be(2);
        found.StopOrderId.Should().Be("stop-2");
        db2.ProtectiveStopOrders.Count().Should().Be(1, "1 エントリー = 高々 1 保護（最新試行のみ）");
    }

    [Fact]
    public void FindActiveはActiveだけを古い順に返しCompletedを含めない()
    {
        var dbName = Guid.NewGuid().ToString();
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        var done = Guid.NewGuid();

        using (var db = NewContext(dbName))
        {
            var store = new EfProtectiveStopOrderStore(db);
            store.Save(Stop(newer, createdAt: Now));
            store.Save(Stop(older, createdAt: Now.AddMinutes(-10)));
            store.Save(Stop(done, state: ProtectiveStopState.Completed));
        }

        using var db2 = NewContext(dbName);
        var active = new EfProtectiveStopOrderStore(db2).FindActive(10);
        active.Select(s => s.EntryDecisionId).Should().Equal(older, newer);
    }

    [Fact]
    public void FindActiveはバッチサイズで打ち切る()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
        {
            var store = new EfProtectiveStopOrderStore(db);
            for (var i = 0; i < 5; i++)
                store.Save(Stop(Guid.NewGuid(), createdAt: Now.AddMinutes(i)));
        }

        using var db2 = NewContext(dbName);
        new EfProtectiveStopOrderStore(db2).FindActive(3).Should().HaveCount(3);
    }
}
