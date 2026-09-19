using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, FR-11, UC-06, #849, IADR-0350 決定 1/2: 乖離の取り込みを支える 2 つの EF ストア
// （取り込み行＝取引台帳の一部／最新の観測＝単一行）を InMemory DB で検証する。
// 別コンテキストは「別レプリカ／再起動」の代理。
public class EfPositionDriftAdoptionStoresTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 18, 15, 40, 0, TimeSpan.Zero);

    private static RiskManagementDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    private static LedgerDriftAdoption Adoption(DateTimeOffset? observedAt = null) => new(
        Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Sell, 3_381, 335.1225m, 1m,
        LedgerQuantityBefore: 3_381, BrokerQuantity: 0, observedAt ?? At.AddMinutes(-5),
        "endazon", "手動売却", At);

    // T-10-471: 取り込み行は永続し、別コンテキストの GetFills へ**取り込み行として**合流する（約定と区別できる）。
    [Fact]
    public void 取り込み行は永続し別コンテキストの約定列へ取り込み行として合流する()
    {
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();

        using (var db = NewContext(dbName))
        {
            var store = new EfPortfolioLedgerStore(db);
            store.AppendApproval(
                decisionId,
                new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
                    BrokerProvider.MoomooSimulate, 3_381, 335.1225m, PositionEffect.Open, 320m),
                At.AddDays(-1));
            store.AppendFill(decisionId, "ORD-1", 3_381, 335.1225m, At.AddDays(-1));
            store.AppendDriftAdoption(Adoption()).Should().BeTrue();
        }

        using var db2 = NewContext(dbName);
        var fills = new EfPortfolioLedgerStore(db2).GetFills();

        fills.Should().HaveCount(2);
        fills.Single(f => !f.IsDriftAdoption).Quantity.Should().Be(3_381);
        var adopted = fills.Single(f => f.IsDriftAdoption);
        adopted.Side.Should().Be(TradeSide.Sell);
        adopted.Quantity.Should().Be(3_381);
        adopted.ExecutedAt.Should().Be(At);
        adopted.DecisionId.Should().Be(Guid.Empty, "承認行を持たない（注文ではない）");
        PortfolioProjection.ProjectOpenPositions(fills).Should().BeEmpty();

        // 誰が・なぜ・何を観測して取り込んだかが行に残る（監査イベントが届かなくても台帳だけで辿れる）。
        var row = db2.PositionDriftAdoptions.Single();
        row.Actor.Should().Be("endazon");
        row.Reason.Should().Be("手動売却");
        row.LedgerQuantityBefore.Should().Be(3_381);
        row.BrokerQuantity.Should().Be(0);
        row.ObservedAtUtc.Should().Be(At.AddMinutes(-5));
    }

    // T-10-472: 同じ観測に対する同じ取り込みは、別レプリカからでも 1 件に絞られる（二重に減らない）。
    [Fact]
    public void 同じ観測に対する同じ取り込みは別コンテキストからでも一件に絞られる()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
            new EfPortfolioLedgerStore(db).AppendDriftAdoption(Adoption()).Should().BeTrue();

        using var db2 = NewContext(dbName);
        var store = new EfPortfolioLedgerStore(db2);

        store.AppendDriftAdoption(Adoption()).Should().BeFalse("Id が違っても冪等キーが同じ");
        db2.PositionDriftAdoptions.Count().Should().Be(1);
        // 観測が違えば別の取り込みである（同じ銘柄で後日ふたたび乖離した場合を塞がない）。
        store.AppendDriftAdoption(Adoption(At.AddDays(1))).Should().BeTrue();
    }

    // T-10-475: 最新の観測は別コンテキストからも読め、逆行する観測（再送・順序前後）では古い値へ戻らない。
    [Fact]
    public void 最新の観測は永続し逆行する観測では戻らない()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
        {
            var store = new EfBrokerPositionObservationStore(db);
            store.GetLatest().Should().BeNull("一度も観測が届いていなければ null（＝不明）");
            store.Record([new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 3_381, 335.12m)], At.AddMinutes(-20));
            store.Record([], At.AddMinutes(-10));
        }

        using (var db = NewContext(dbName))
            new EfBrokerPositionObservationStore(db).Record(
                [new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 3_381, 335.12m)], At.AddMinutes(-15));

        using var db2 = NewContext(dbName);
        var latest = new EfBrokerPositionObservationStore(db2).GetLatest();

        latest.Should().NotBeNull();
        latest!.ObservedAt.Should().Be(At.AddMinutes(-10));
        latest.Positions.Should().BeEmpty("空列は『建玉が無いと観測した』事実として保持される（null＝不明とは別）");
    }

    // T-10-476: 保持している観測が読めない（壊れた JSON）ときは**不明**として扱う。
    // 空列（＝全建玉が消えたという観測）へ倒すと、取り込みで台帳の建玉が消える。
    [Fact]
    public void 観測が読めなければ不明として扱い空列へ倒さない()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
        {
            db.BrokerPositionObservations.Add(new BrokerPositionObservationRow
            {
                PositionsJson = "{ not json",
                ObservedAtUtc = At,
                UpdatedAt = At,
            });
            db.SaveChanges();
        }

        using var db2 = NewContext(dbName);
        new EfBrokerPositionObservationStore(db2).GetLatest().Should().BeNull();
    }

    // T-10-475 の対: インメモリ実装も同じ意味論（逆行を無視・未観測は null）。
    [Fact]
    public void インメモリの観測ストアも逆行を無視し未観測はnullを返す()
    {
        var store = new InMemoryBrokerPositionObservationStore();
        store.GetLatest().Should().BeNull();

        store.Record([], At);
        store.Record([new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 1, 1m)], At.AddMinutes(-1));

        store.GetLatest()!.ObservedAt.Should().Be(At);
        store.GetLatest()!.Positions.Should().BeEmpty();
    }
}
