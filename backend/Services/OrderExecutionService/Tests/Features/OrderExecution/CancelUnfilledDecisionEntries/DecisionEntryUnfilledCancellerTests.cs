using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.AmendOrder;
using OrderExecutionService.Features.OrderExecution.CancelUnfilledDecisionCloses;
using OrderExecutionService.Features.OrderExecution.CancelUnfilledDecisionEntries;
using OrderExecutionService.Infrastructure.Persistence;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-3080, FR-10, FR-19, #1486, IADR-0594 決定1: 判断由来の**新規建て**の指値が発注から N 分未約定なら系が取り消すだけにする。
// 既定は無効（計画の裁定待ち＝planning#852）。形は決済の取消（DecisionCloseUnfilledCanceller）に倣う。
public class DecisionEntryUnfilledCancellerTests
{
    // 2026-10-27 14:00Z = 米国東部 10:00（EDT）。
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 14, 0, 0, TimeSpan.Zero);

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class Broker : IBrokerAdapter
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public Dictionary<string, BrokerOrder?> Orders { get; } = [];

        public List<string> Cancelled { get; } = [];

        public List<OrderIntent> Placed { get; } = [];

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult(Orders.TryGetValue(orderId, out var o) ? o : null);

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            Placed.Add(intent);
            throw new InvalidOperationException("取消の後に発注してはならない（置き直さない）");
        }

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            Cancelled.Add(orderId);
            if (Orders.TryGetValue(orderId, out var o) && o is not null)
                Orders[orderId] = o with { Status = OrderStatus.Cancelled, CompletedAt = Now };
            return Task.CompletedTask;
        }
    }

    private static ExecutionRecord Order(
        string orderId,
        DateTimeOffset placedAt,
        PositionEffect effect = PositionEffect.Open,
        OrderApprovalOrigin? origin = OrderApprovalOrigin.TradeDecision,
        OrderStatus status = OrderStatus.Accepted,
        int filled = 0,
        string symbol = "TSLA",
        TradeSide side = TradeSide.Buy) =>
        new(Guid.NewGuid(), orderId, symbol, Market.UnitedStates, side, ProductType.Cash, effect, 115, 397.07m, filled,
            filled > 0 ? 397.07m : 0m, status, 0m, placedAt, ApprovalOrigin: origin);

    private sealed record Fixture(IExecutedOrderStore Store, Broker Broker, MutableClock Clock, DecisionEntryUnfilledCanceller Canceller);

    private static Fixture Build(DecisionEntryUnfilledCancelOptions options, params ExecutionRecord[] records)
    {
        var store = new InMemoryExecutedOrderStore();
        var broker = new Broker();
        foreach (var r in records)
            Save(store, broker, r);
        var clock = new MutableClock(Now);
        var amendments = new OrderAmendmentService(broker, store, new InMemoryOrderLifecycleStore(), clock);
        return new Fixture(store, broker, clock, new DecisionEntryUnfilledCanceller(store, amendments, clock, Options.Create(options)));
    }

    private static void Save(IExecutedOrderStore store, Broker broker, ExecutionRecord r)
    {
        store.Save(r);
        broker.Orders[r.OrderId] = new BrokerOrder(
            r.OrderId,
            new OrderIntent(r.Symbol, r.Market, r.Side, r.ProductType, BrokerProvider.MoomooSimulate, r.Quantity, 397.07m, r.PositionEffect),
            r.Status, r.FilledQuantity, r.AveragePrice, r.ExecutedAt, null);
    }

    private static DecisionEntryUnfilledCancelOptions Enabled(int max = 1) => new() { Enabled = true, MaxCancelsPerPositionPerDay = max };

    [Fact]
    public async Task T_10_3080_既定は無効で何もしない()
    {
        new DecisionEntryUnfilledCancelOptions().Enabled.Should().BeFalse("計画の裁定待ち（planning#852）。有効化は裁定の後");
        new DecisionEntryUnfilledCancelOptions().UnfilledMinutes.Should().Be(DecisionCloseUnfilledCancelDefaults.UnfilledMinutes, "暫定の初期値は決済側を共用");

        var f = Build(new DecisionEntryUnfilledCancelOptions(), Order("TSLA-1", Now.AddHours(-3)));

        (await f.Canceller.RunOnceAsync()).Should().BeSameAs(DecisionCloseUnfilledCancelResult.Empty);
        f.Broker.Cancelled.Should().BeEmpty();
    }

    // 窓の両端: 発注から N 分以上（前の端）かつ非終端（後の端）の判断由来の新規建てだけを取り消す。決済・利用者や不明の出どころ・約定済みは取り消さない。
    [Fact]
    public async Task T_10_3080_N分未約定の判断由来の新規建てだけを取り消し置き直さない()
    {
        var f = Build(Enabled(),
            Order("ENTRY-STALE", Now.AddMinutes(-15)),
            Order("ENTRY-FRESH", Now.AddMinutes(-14), symbol: "NVDA"),
            Order("ENTRY-PARTIAL", Now.AddMinutes(-30), status: OrderStatus.PartiallyFilled, filled: 40, symbol: "AMD"),
            Order("ENTRY-FILLED", Now.AddMinutes(-40), status: OrderStatus.Filled, filled: 115, symbol: "AAPL"),
            Order("CLOSE", Now.AddMinutes(-40), effect: PositionEffect.Close, side: TradeSide.Sell, symbol: "IONQ"),
            Order("UNKNOWN", Now.AddMinutes(-40), origin: OrderApprovalOrigin.Unknown, symbol: "MSFT"),
            Order("NO-ORIGIN", Now.AddMinutes(-40), origin: null, symbol: "META"));

        var result = await f.Canceller.RunOnceAsync();

        f.Broker.Cancelled.Should().BeEquivalentTo(["ENTRY-PARTIAL", "ENTRY-STALE"]);
        f.Broker.Placed.Should().BeEmpty("置き直し・成行への切り替えはしない");
        result.Confirmed.Should().OnlyContain(c => c.Reason.StartsWith(DecisionEntryUnfilledCanceller.ReasonPrefix, StringComparison.Ordinal));
        result.Confirmed.Single(c => c.OrderId == "ENTRY-PARTIAL").ObservedFilledQuantity.Should().Be(40, "一部約定は残りだけを取り消す");
        f.Store.GetAll().Single(r => r.OrderId == "ENTRY-STALE").UnfilledCancelRequestedAt.Should().Be(Now);
        f.Store.GetAll().Single(r => r.OrderId == "CLOSE").UnfilledCancelRequestedAt.Should().BeNull("決済は新規建ての取消の対象ではない");

        f.Clock.UtcNow = Now.AddMinutes(1);
        await f.Canceller.RunOnceAsync();
        f.Broker.Cancelled.Should().BeEquivalentTo(["ENTRY-PARTIAL", "ENTRY-STALE", "ENTRY-FRESH"], "N 分を過ぎたら取り消す。同じ注文は二度取り消さない");
    }

    // 1 建玉（銘柄・市場・新規建ての方向）・1 市場の現地取引日あたり上限まで。決済の取消の回数とは別に数える。
    [Fact]
    public async Task T_10_3080_回数の上限は新規建ての建玉ごとで決済の取消と別に数える()
    {
        var f = Build(Enabled(), Order("A-1", Now.AddMinutes(-30)));
        (await f.Canceller.RunOnceAsync()).Requested.Should().ContainSingle();

        // 同じ建玉（TSLA の買いの新規建て）の 2 本目は取り消さない。売りの新規建て（別の方向）は取り消す。
        Save(f.Store, f.Broker, Order("A-2", Now.AddMinutes(-20)));
        Save(f.Store, f.Broker, Order("A-SHORT", Now.AddMinutes(-20), side: TradeSide.Sell));
        // 同じ銘柄の決済の取消の印（別の建玉効果）は数えない。
        var close = Order("C-1", Now.AddMinutes(-20), effect: PositionEffect.Close);
        Save(f.Store, f.Broker, close);
        f.Store.MarkUnfilledCancelRequested("C-1", Now).Should().BeTrue();

        var second = await f.Canceller.RunOnceAsync();

        second.Capped.Select(r => r.OrderId).Should().Equal(["A-2"]);
        second.Requested.Select(r => r.OrderId).Should().Equal(["A-SHORT"]);
    }

    // 問い合わせ（建玉効果つき）の意味論を DB とインメモリで同一に固定する。
    [Theory]
    [InlineData("ef")]
    [InlineData("inmemory")]
    public void T_10_3080_新規建ての問い合わせと数えはDBとインメモリで同一(string kind)
    {
        var store = Stores(kind);
        store().Save(Order("open-old", Now.AddMinutes(-40)));
        store().Save(Order("open-fresh", Now.AddMinutes(-14)));
        store().Save(Order("close-old", Now.AddMinutes(-40), effect: PositionEffect.Close, side: TradeSide.Sell));
        store().Save(Order("open-owner", Now.AddMinutes(-40), origin: OrderApprovalOrigin.OwnerClose));

        store().FindUnfilledDecisionOrders(PositionEffect.Open, Now.AddMinutes(-15), 100).Select(r => r.OrderId).Should().Equal(["open-old"]);
        store().FindUnfilledDecisionOrders(PositionEffect.Close, Now.AddMinutes(-15), 100).Select(r => r.OrderId).Should().Equal(["close-old"]);
        store().FindUnfilledDecisionCloses(Now.AddMinutes(-15), 100).Select(r => r.OrderId).Should().Equal(["close-old"], "決済の口は従来どおり");

        store().MarkUnfilledCancelRequested("open-old", Now).Should().BeTrue();
        store().CountUnfilledCancelRequests(PositionEffect.Open, "TSLA", Market.UnitedStates, TradeSide.Buy, Now, Now.AddDays(1)).Should().Be(1);
        store().CountUnfilledCancelRequests(PositionEffect.Close, "TSLA", Market.UnitedStates, TradeSide.Buy, Now, Now.AddDays(1)).Should().Be(0);
        store().CountUnfilledCancelRequests("TSLA", Market.UnitedStates, TradeSide.Buy, Now, Now.AddDays(1)).Should().Be(0, "決済の数えは新規建てを数えない");
    }

    // 有効時の範囲の検証は決済の取消と同じ（N は相場操縦の検知の窓より長く 390 分以下・回数 1〜3）。無効なら検証しない。
    [Theory]
    [InlineData(false, 1, 0, true)]
    [InlineData(true, 15, 1, true)]
    [InlineData(true, 5, 1, false)]
    [InlineData(true, 391, 1, false)]
    [InlineData(true, 15, 4, false)]
    public void T_10_3080_有効時の範囲を起動で検証する(bool enabled, int minutes, int max, bool valid)
    {
        var options = new DecisionEntryUnfilledCancelOptions { Enabled = enabled, UnfilledMinutes = minutes, MaxCancelsPerPositionPerDay = max };

        var result = new DecisionEntryUnfilledCancelOptionsValidator().Validate(null, options);

        result.Succeeded.Should().Be(valid);
        if (!valid)
            result.FailureMessage.Should().Contain(DecisionEntryUnfilledCancelOptions.SectionName);
    }

    private static Func<IExecutedOrderStore> Stores(string kind)
    {
        if (kind != "ef")
        {
            var shared = new InMemoryExecutedOrderStore();
            return () => shared;
        }

        var dbName = Guid.NewGuid().ToString();
        return () => new EfExecutedOrderStore(new OrderExecutionDbContext(
            new DbContextOptionsBuilder<OrderExecutionDbContext>().UseInMemoryDatabase(dbName).Options));
    }
}
