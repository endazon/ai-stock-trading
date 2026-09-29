using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using Xunit;

namespace OrderExecutionService.Tests;

// T-10-1765, FR-10, FR-05, #1105, IADR-0461 決定1: 決済の数量から処理中の決済を引くための問い合わせ（FindPendingCloses）の意味論を、
// **本番の DB 実装・インメモリ実装・既定の実装（試験用の包み型）で同一に**固定する（片側だけの乖離を検知する）。
public class ExecutedOrderStorePendingClosesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 15, 13, 56, TimeSpan.Zero);

    public static TheoryData<string> Implementations() => ["ef", "inmemory", "default"];

    // 既定の実装（インターフェースの既定メソッド）だけを使う包み型。
    private sealed class DefaultOnlyStore(IExecutedOrderStore inner) : IExecutedOrderStore
    {
        public void Save(ExecutionRecord record) => inner.Save(record);
        public IReadOnlyList<ExecutionRecord> GetAll() => inner.GetAll();
        public ExecutionRecord? FindByDecisionId(Guid decisionId) => inner.FindByDecisionId(decisionId);
        public IReadOnlyList<ExecutionRecord> FindPendingSince(DateTimeOffset since, int batchSize) =>
            inner.FindPendingSince(since, batchSize);
        public IReadOnlyList<ExecutionRecord> FindPendingByOrderIds(IReadOnlyCollection<string> orderIds) =>
            inner.FindPendingByOrderIds(orderIds);
        public bool RenewTracking(string orderId, DateTimeOffset trackedFrom) => inner.RenewTracking(orderId, trackedFrom);
        public bool UpdateOutcome(string orderId, OrderStatus status, int filledQuantity, decimal averagePrice,
            decimal slippageRatio, DateTimeOffset executedAt) =>
            inner.UpdateOutcome(orderId, status, filledQuantity, averagePrice, slippageRatio, executedAt);
    }

    private static Func<IExecutedOrderStore> Stores(string kind)
    {
        if (kind != "ef")
        {
            var shared = new InMemoryExecutedOrderStore();
            IExecutedOrderStore store = kind == "default" ? new DefaultOnlyStore(shared) : shared;
            return () => store;
        }

        var dbName = Guid.NewGuid().ToString();
        return () => new EfExecutedOrderStore(new OrderExecutionDbContext(
            new DbContextOptionsBuilder<OrderExecutionDbContext>().UseInMemoryDatabase(dbName).Options));
    }

    private static ExecutionRecord Record(
        string orderId, OrderStatus status, DateTimeOffset at, PositionEffect effect = PositionEffect.Close,
        TradeSide side = TradeSide.Sell, string symbol = "AAPL", Market market = Market.UnitedStates, int filled = 0) =>
        new(Guid.NewGuid(), orderId, symbol, market, side, ProductType.Cash, effect, 713, 331m, filled,
            filled > 0 ? 331m : 0m, status, 0m, at);

    // T-10-1765: 非終端の決済だけを、銘柄・市場・方向で絞って古い順に返す。追跡上限（時刻）では切らない。
    [Theory]
    [MemberData(nameof(Implementations))]
    public void 非終端の決済を銘柄と市場と方向で絞って古い順に返す(string kind)
    {
        var store = Stores(kind);
        store().Save(Record("new", OrderStatus.Accepted, Now));
        store().Save(Record("old", OrderStatus.PartiallyFilled, Now.AddDays(-3), filled: 300));
        store().Save(Record("filled", OrderStatus.Filled, Now, filled: 713));
        store().Save(Record("cancelled", OrderStatus.Cancelled, Now));
        store().Save(Record("rejected", OrderStatus.Rejected, Now));
        store().Save(Record("expired", OrderStatus.Expired, Now));
        store().Save(Record("open", OrderStatus.Accepted, Now, effect: PositionEffect.Open));
        store().Save(Record("buy-close", OrderStatus.Accepted, Now, side: TradeSide.Buy));
        store().Save(Record("msft", OrderStatus.Accepted, Now, symbol: "MSFT"));
        store().Save(Record("jp", OrderStatus.Accepted, Now, market: Market.Japan));

        var found = store().FindPendingCloses("AAPL", Market.UnitedStates, TradeSide.Sell);

        found.Select(r => r.OrderId).Should().Equal("old", "new");
        found[0].FilledQuantity.Should().Be(300);
    }

    // T-10-1765: 該当が無ければ空を返す。
    [Theory]
    [MemberData(nameof(Implementations))]
    public void 該当が無ければ空を返す(string kind)
    {
        var store = Stores(kind);
        store().Save(Record("filled", OrderStatus.Filled, Now, filled: 713));

        store().FindPendingCloses("AAPL", Market.UnitedStates, TradeSide.Sell).Should().BeEmpty();
    }
}
