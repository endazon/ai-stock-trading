using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, #829, IADR-0346 決定1/決定5: 未終端の承認済み新規建て注文の供給源（EF / InMemory）と、
// 見送り（OrderDispatchForgone）を注文アクティビティの終端として射影する RecordForgone を検証する。
// 🔴 2 実装を**同じシナリオ**で検査する（InMemory だけが正しい／EF だけが正しいドリフトを防ぐ）。
public class WorkingEntryOrderSourceTests
{
    private static readonly DateTimeOffset Base = new(2026, 9, 17, 15, 0, 0, TimeSpan.Zero);

    private static RiskManagementDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    private static OrderIntent Intent(
        PositionEffect effect = PositionEffect.Open, string symbol = "AAPL", Market market = Market.UnitedStates,
        int qty = 848, decimal price = 334.01m, decimal fxRateToBase = 1m) =>
        new(symbol, market, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, qty, price, effect,
            StopLossPrice: null, FxRateToBase: fxRateToBase);

    private sealed record Ids(Guid NoActivityRow, Guid Working, Guid Cancelled, Guid Close, Guid BeforeBound, Guid Forgone, Guid Foreign, Guid FilledThenStale);

    // 承認は ledger へ、生死は activity へ（本番のハンドラチェーンと同じ 2 射影）。
    private static Ids Seed(IPortfolioLedgerStore ledger, IOrderActivityStore activity)
    {
        var ids = new Ids(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var at = Base.AddMinutes(1);

        void Approve(Guid id, OrderIntent intent, DateTimeOffset approvedAt, bool withActivity = true)
        {
            ledger.AppendApproval(id, intent, approvedAt);
            if (withActivity)
                activity.RecordPlacement(id, intent.Symbol, intent.Market, intent.Side, intent.Quantity, approvedAt);
        }

        // 注文アクティビティの射影が未着（行なし）＝未終端に倒す。
        Approve(ids.NoActivityRow, Intent(), at, withActivity: false);

        // 部分約定のまま生きている。
        Approve(ids.Working, Intent(), at);
        activity.RecordExecution(ids.Working, OrderStatus.PartiallyFilled, 100, at.AddSeconds(5));

        // 約定ゼロで取消（実測 2026-09-17 の 10 件と同じ形）。
        Approve(ids.Cancelled, Intent(), at);
        activity.RecordExecution(ids.Cancelled, OrderStatus.Cancelled, 0, at.AddSeconds(30));

        // 決済は算入しない（計画 FR-10「手仕舞い注文は算入しない」）。
        Approve(ids.Close, Intent(PositionEffect.Close), at);

        // 窓の下限より前。
        Approve(ids.BeforeBound, Intent(), Base.AddMinutes(-1));

        // 見送り（発注されていない）。
        Approve(ids.Forgone, Intent(), at);
        activity.RecordForgone(ids.Forgone, "AAPL", Market.UnitedStates, TradeSide.Buy, 848, at.AddSeconds(1));

        // 外貨建て（承認時レートを運ぶ）。
        Approve(ids.Foreign, Intent(symbol: "7203", market: Market.Japan, qty: 100, price: 3_000m, fxRateToBase: 0.0068m), at);

        // 終端（Filled）の後に遅着の非終端イベントが届いても生き返らない（TerminalAt は単調）。
        Approve(ids.FilledThenStale, Intent(), at);
        activity.RecordExecution(ids.FilledThenStale, OrderStatus.Filled, 848, at.AddSeconds(10));
        activity.RecordExecution(ids.FilledThenStale, OrderStatus.Accepted, 0, at.AddSeconds(11));

        return ids;
    }

    private static void AssertWorking(IReadOnlyList<WorkingEntryOrder> result, Ids ids)
    {
        result.Select(o => o.DecisionId).Should().BeEquivalentTo(new[] { ids.NoActivityRow, ids.Working, ids.Foreign });

        var foreign = result.Single(o => o.DecisionId == ids.Foreign);
        foreign.Symbol.Should().Be("7203");
        foreign.Market.Should().Be(Market.Japan);
        foreign.Side.Should().Be(TradeSide.Buy);
        foreign.Quantity.Should().Be(100);
        foreign.Price.Should().Be(3_000m);
        foreign.FxRateToBase.Should().Be(0.0068m);
        foreign.ApprovedAt.Should().Be(Base.AddMinutes(1));
    }

    // T-10-338: 供給源は Open・未終端（TerminalAt なし／行なし）・下限以降だけを返す。
    [Fact]
    public void EF実装は新規建てかつ未終端で下限以降の承認だけを返す()
    {
        var dbName = Guid.NewGuid().ToString();
        Ids ids;
        using (var db = NewContext(dbName))
        {
            ids = Seed(new EfPortfolioLedgerStore(db), new EfOrderActivityStore(db));
        }

        using var db2 = NewContext(dbName);
        AssertWorking(new EfWorkingEntryOrderSource(db2).GetWorkingEntryOrders(Base), ids);
    }

    [Fact]
    public void InMemory実装もEF実装と同じ注文を返す()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var activity = new InMemoryOrderActivityStore();
        var ids = Seed(ledger, activity);

        AssertWorking(new InMemoryWorkingEntryOrderSource(ledger, activity).GetWorkingEntryOrders(Base), ids);
    }

    // IADR-0107: 列追加前の承認行（FxRateToBase が null）はレート 1＝基準通貨建て（GetFills と同じ扱い）。
    [Fact]
    public void EF実装はレート未記録の承認をレート1として返す()
    {
        var dbName = Guid.NewGuid().ToString();
        var id = Guid.NewGuid();
        using (var db = NewContext(dbName))
        {
            db.ApprovedOrders.Add(new ApprovedOrderRow
            {
                DecisionId = id,
                Symbol = "AAPL",
                Market = Market.UnitedStates,
                Side = TradeSide.Buy,
                ProductType = ProductType.Cash,
                PositionEffect = PositionEffect.Open,
                Mode = BrokerProvider.MoomooSimulate,
                Quantity = 10,
                Price = 100m,
                FxRateToBase = null,
                ApprovedAt = Base.AddMinutes(1),
            });
            db.SaveChanges();
        }

        using var db2 = NewContext(dbName);
        new EfWorkingEntryOrderSource(db2).GetWorkingEntryOrders(Base)
            .Should().ContainSingle().Which.FxRateToBase.Should().Be(1m);
    }

    // ── IADR-0346 決定5: 見送りの射影 ──

    public static TheoryData<string> Stores => new() { "InMemory", "Ef" };

    private static (IOrderActivityStore Store, Func<Guid, (OrderStatus Status, DateTimeOffset? TerminalAt, int Quantity)?> Read) NewStore(string kind)
    {
        if (kind == "InMemory")
        {
            var mem = new InMemoryOrderActivityStore();
            return (mem, id => mem.GetRecentActivity("AAPL", Market.UnitedStates, Base.AddHours(1), TimeSpan.FromHours(2))
                .Records.Select(r => ((OrderStatus, DateTimeOffset?, int)?)(r.Status, r.TerminalAt, r.Quantity)).SingleOrDefault());
        }

        var db = NewContext(Guid.NewGuid().ToString());
        return (new EfOrderActivityStore(db), id => db.OrderActivities.Find(id) is { } row
            ? (row.Status, row.TerminalAt, row.Quantity)
            : null);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void 見送りは生きている注文を拒否として終端にする(string kind)
    {
        var (store, read) = NewStore(kind);
        var id = Guid.NewGuid();
        store.RecordPlacement(id, "AAPL", Market.UnitedStates, TradeSide.Buy, 848, Base);

        store.RecordForgone(id, "AAPL", Market.UnitedStates, TradeSide.Buy, 848, Base.AddSeconds(2));

        read(id).Should().Be((OrderStatus.Rejected, (DateTimeOffset?)Base.AddSeconds(2), 848));
    }

    // 到着順序に依存しない: 見送りが承認の射影より先に届いても終端が残り、後着の承認で生き返らない。
    [Theory]
    [MemberData(nameof(Stores))]
    public void 見送りが承認より先に届いても終端の行が残る(string kind)
    {
        var (store, read) = NewStore(kind);
        var id = Guid.NewGuid();

        store.RecordForgone(id, "AAPL", Market.UnitedStates, TradeSide.Buy, 848, Base.AddSeconds(2));
        store.RecordPlacement(id, "AAPL", Market.UnitedStates, TradeSide.Buy, 848, Base);

        read(id).Should().Be((OrderStatus.Rejected, (DateTimeOffset?)Base.AddSeconds(2), 848));
    }

    // 既に終端の注文（取消・約定）を見送りで上書きしない（相場操縦検知の母集団〔約定なし取消〕を動かさない）。
    [Theory]
    [MemberData(nameof(Stores))]
    public void 既に終端の注文は見送りで変えない(string kind)
    {
        var (store, read) = NewStore(kind);
        var id = Guid.NewGuid();
        store.RecordPlacement(id, "AAPL", Market.UnitedStates, TradeSide.Buy, 848, Base);
        store.RecordCancellation(id, Base.AddSeconds(1));

        store.RecordForgone(id, "AAPL", Market.UnitedStates, TradeSide.Buy, 848, Base.AddSeconds(2));

        read(id).Should().Be((OrderStatus.Cancelled, (DateTimeOffset?)Base.AddSeconds(1), 848));
    }

    // ---- FR-10, #1319, IADR-0529 決定3: 予約（承認行がまだ無い承認）を同じ規則で返す ----

    private sealed record ReservationIds(Guid ReservedOnly, Guid Projected, Guid ForgoneBeforeProjection, Guid BeforeBound);

    private static EntryReservation Reservation(Guid id, DateTimeOffset approvedAt, string symbol = "NVDA") =>
        new(id, symbol, Market.UnitedStates, TradeSide.Buy, 10, 100m, 1m, 10, approvedAt, StopLossExecutionMethod.BrokerStopOrder);

    // 予約は審査が書き、承認行・注文アクティビティは OrderApproved の射影が書く（本番と同じ 3 つの書き手）。
    private static ReservationIds SeedReservations(
        IEntryReservationStore reservations, IPortfolioLedgerStore ledger, IOrderActivityStore activity)
    {
        var ids = new ReservationIds(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var at = Base.AddMinutes(1);

        // 承認行がまだ無い（OrderApproved の往復の途中）＝予約で数える。
        reservations.Reserve(Reservation(ids.ReservedOnly, at));

        // 承認行へ引き継がれた＝承認行の側で 1 回だけ数える。
        reservations.Reserve(Reservation(ids.Projected, at, "AMZN"));
        var intent = new OrderIntent("AMZN", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 100m);
        ledger.AppendApproval(ids.Projected, intent, at);
        activity.RecordPlacement(ids.Projected, "AMZN", Market.UnitedStates, TradeSide.Buy, 10, at);

        // 承認行より先に見送り（終端）が届いた。
        reservations.Reserve(Reservation(ids.ForgoneBeforeProjection, at, "META"));
        activity.RecordForgone(ids.ForgoneBeforeProjection, "META", Market.UnitedStates, TradeSide.Buy, 10, at.AddSeconds(1));

        // 窓の下限より前。
        reservations.Reserve(Reservation(ids.BeforeBound, Base.AddMinutes(-1), "GOOG"));
        return ids;
    }

    private static void AssertReservations(IReadOnlyList<WorkingEntryOrder> result, ReservationIds ids)
    {
        result.Select(o => o.DecisionId).Should().BeEquivalentTo(
            new[] { ids.ReservedOnly, ids.Projected },
            "承認行の無い予約は返し、承認行のある予約は承認行として 1 回だけ返し、終端・下限より前は返さない");
        result.Should().ContainSingle(o => o.DecisionId == ids.Projected, "予約と承認行の両方で数えない");
        result.Single(o => o.DecisionId == ids.ReservedOnly).Should().Be(
            new WorkingEntryOrder(ids.ReservedOnly, "NVDA", Market.UnitedStates, TradeSide.Buy, 10, 100m, Base.AddMinutes(1), 1m));
    }

    // 🔴 T-10-2577: 注文源（EF / InMemory）は承認行の無い予約を未終端の新規建てとして返し、承認行へ引き継がれた予約は
    // 承認行として 1 回だけ返す（二重計上しない）。
    [Fact]
    public void EF実装は承認行の無い予約を返し承認行がある予約を二重に返さない()
    {
        var dbName = Guid.NewGuid().ToString();
        ReservationIds ids;
        using (var db = NewContext(dbName))
        {
            ids = SeedReservations(new EfEntryReservationStore(db), new EfPortfolioLedgerStore(db), new EfOrderActivityStore(db));
        }

        using var db2 = NewContext(dbName);
        AssertReservations(new EfWorkingEntryOrderSource(db2).GetWorkingEntryOrders(Base), ids);
    }

    [Fact]
    public void InMemory実装も予約をEF実装と同じ規則で返す()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var activity = new InMemoryOrderActivityStore();
        var reservations = new InMemoryEntryReservationStore();
        var ids = SeedReservations(reservations, ledger, activity);

        AssertReservations(
            new InMemoryWorkingEntryOrderSource(ledger, activity, reservations).GetWorkingEntryOrders(Base), ids);
    }

    // T-10-2580: 予約は判断 ID で冪等（同じ判断を二度書いても 1 行・最初の値のまま）で、引けば同じ値が返る。
    [Fact]
    public void EF実装の予約は判断IDで冪等で引けば同じ値が返る()
    {
        var dbName = Guid.NewGuid().ToString();
        var id = Guid.NewGuid();
        using (var db = NewContext(dbName))
        {
            var store = new EfEntryReservationStore(db);
            store.Reserve(Reservation(id, Base));
            store.Reserve(Reservation(id, Base.AddMinutes(5), "OTHER"));
        }

        using var db2 = NewContext(dbName);
        db2.EntryReservations.Count().Should().Be(1);
        new EfEntryReservationStore(db2).Find(id).Should().Be(Reservation(id, Base));
        new EfEntryReservationStore(db2).Find(Guid.NewGuid()).Should().BeNull();
    }

    // T-10-2581: InMemory 実装は例外で終わった臨界区間の予約を残さない（EF 実装のロールバックと同じ意味論）。
    [Fact]
    public async Task InMemory実装は例外で終わった臨界区間の予約を残さない()
    {
        var store = new InMemoryEntryReservationStore();
        var id = Guid.NewGuid();

        var act = () => store.RunSerializedAsync<int>(_ =>
        {
            store.Reserve(Reservation(id, Base));
            throw new InvalidOperationException("発行前に落ちた");
        }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        store.Find(id).Should().BeNull();
    }
}
