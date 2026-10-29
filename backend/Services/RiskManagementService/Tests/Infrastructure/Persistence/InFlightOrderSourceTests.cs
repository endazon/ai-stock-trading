using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Persistence;
using Xunit;

namespace RiskManagementService.Tests;

// NFR, #1502, IADR-0600: 未終端の注文（新規建て・決済）の供給源（EF / InMemory）を**同じシナリオ**で検査する
// （WorkingEntryOrderSourceTests と同じ作り。片方だけが正しいドリフトを防ぐ）。
public class InFlightOrderSourceTests
{
    private static readonly DateTimeOffset Base = new(2026, 10, 29, 15, 0, 0, TimeSpan.Zero);

    private static RiskManagementDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>().UseInMemoryDatabase(dbName).Options);

    private static OrderIntent Intent(PositionEffect effect, string symbol, TradeSide side = TradeSide.Buy) =>
        new(symbol, Market.UnitedStates, side, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 100m, effect,
            StopLossPrice: null, FxRateToBase: 1m);

    private static EntryReservation Reservation(Guid id, DateTimeOffset approvedAt, string symbol) =>
        new(id, symbol, Market.UnitedStates, TradeSide.Buy, 10, 100m, 1m, 10, approvedAt, StopLossExecutionMethod.BrokerStopOrder);

    // 承認行は ledger、生死は activity、予約は reservations（本番の 3 つの書き手）。
    private static void Seed(IPortfolioLedgerStore ledger, IOrderActivityStore activity, IEntryReservationStore reservations)
    {
        var at = Base.AddMinutes(1);

        void Approve(OrderIntent intent, DateTimeOffset approvedAt, bool withActivity = true, Guid? id = null)
        {
            var decision = id ?? Guid.NewGuid();
            ledger.AppendApproval(decision, intent, approvedAt);
            if (withActivity)
                activity.RecordPlacement(decision, intent.Symbol, intent.Market, intent.Side, intent.Quantity, approvedAt);
        }

        // 返すもの: 未約定の新規建て（射影の未着＝行なしを含む）・手仕舞い中の決済・承認行の無い予約。
        Approve(Intent(PositionEffect.Open, "AAPL"), at);
        Approve(Intent(PositionEffect.Open, "MSFT"), at.AddSeconds(1), withActivity: false);
        Approve(Intent(PositionEffect.Close, "HOOD", TradeSide.Sell), at.AddSeconds(2));
        reservations.Reserve(Reservation(Guid.NewGuid(), at.AddSeconds(3), "NVDA"));

        // 返さないもの: 終端（取消・約定・見送り）・窓の下限より前・無効の予約・承認行へ引き継がれた予約の予約側。
        var cancelled = Guid.NewGuid();
        Approve(Intent(PositionEffect.Close, "TSLA", TradeSide.Sell), at, id: cancelled);
        activity.RecordCancellation(cancelled, at.AddSeconds(30));
        var filled = Guid.NewGuid();
        Approve(Intent(PositionEffect.Open, "META"), at, id: filled);
        activity.RecordExecution(filled, OrderStatus.Filled, 10, at.AddSeconds(10));
        var forgone = Guid.NewGuid();
        Approve(Intent(PositionEffect.Open, "AMD"), at, id: forgone);
        activity.RecordForgone(forgone, "AMD", Market.UnitedStates, TradeSide.Buy, 10, at.AddSeconds(1));
        Approve(Intent(PositionEffect.Close, "OLD", TradeSide.Sell), Base.AddMinutes(-1));
        var voided = Guid.NewGuid();
        reservations.Reserve(Reservation(voided, at, "VOID"));
        reservations.Void(voided, at.AddMinutes(1), [RejectionReason.KillSwitchActive]);
        var projected = Guid.NewGuid();
        reservations.Reserve(Reservation(projected, at, "AMZN"));
        Approve(Intent(PositionEffect.Open, "AMZN"), at, id: projected);
    }

    private static void AssertInFlight(IReadOnlyList<InFlightOrder> result) =>
        result.Select(o => (o.Symbol, o.PositionEffect, o.State)).Should().BeEquivalentTo(new[]
        {
            ("AAPL", PositionEffect.Open, InFlightOrderState.Approved),
            ("MSFT", PositionEffect.Open, InFlightOrderState.Approved),
            ("HOOD", PositionEffect.Close, InFlightOrderState.Approved),
            ("NVDA", PositionEffect.Open, InFlightOrderState.Reserved),
            ("AMZN", PositionEffect.Open, InFlightOrderState.Approved),
        });

    [Fact]
    public void EF実装は新規建てと決済の未終端と承認行の無い予約だけを返す_1502()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
        {
            Seed(new EfPortfolioLedgerStore(db), new EfOrderActivityStore(db), new EfEntryReservationStore(db));
        }

        using var db2 = NewContext(dbName);
        AssertInFlight(new EfInFlightOrderSource(db2).GetInFlightOrders(Base));
    }

    [Fact]
    public void InMemory実装もEF実装と同じ注文を返す_1502()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var activity = new InMemoryOrderActivityStore();
        var reservations = new InMemoryEntryReservationStore();
        Seed(ledger, activity, reservations);

        AssertInFlight(new InMemoryInFlightOrderSource(ledger, activity, reservations).GetInFlightOrders(Base));
    }

    // 🔴 承認行と予約を 1 つの SQL 文（UNION ALL）で読む（2 文だと、間にコミットされた承認がどちらにも現れない）。
    // 本番の訳（Npgsql）で確かめる。接続は張らない（ToQueryString は訳すだけ）。
    [Fact]
    public void EF実装は承認行と予約を1つのSQL文で読む_1502()
    {
        using var db = new RiskManagementDbContext(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseNpgsql("Host=localhost;Database=unused").Options);

        var sql = new EfInFlightOrderSource(db).Query(Base).ToQueryString();

        sql.Should().Contain("UNION ALL");
    }
}
