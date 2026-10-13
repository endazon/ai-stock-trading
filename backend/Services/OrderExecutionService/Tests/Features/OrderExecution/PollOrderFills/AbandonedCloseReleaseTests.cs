using Microsoft.EntityFrameworkCore;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.PollOrderFills;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 FR-10, FR-05, #1339（利用者裁定 2026-10-14・最小是正）, IADR-0536 決定2・3: 追跡を打ち切った承認経路の決済は、
// 打ち切りの後も照会し直し、**ブローカーが終端と答えたときだけ**解放する（記録の終端化・OrderExecuted）。
// 非終端・不明（null）・例外は解放しない（迷ったら解放しない＝解放した注文が生きていれば同じ建玉へ 2 本目の決済が並び得る）。
// 作業仕様書 20261014_1339_close-limit-tif-and-release §受け入れ基準 3〜5・8。
public class AbandonedCloseReleaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 14, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MaxTracking = TimeSpan.FromHours(24);
    private static readonly DateTimeOffset PlacedAt = Now.AddHours(-24.5); // 10-13 13:30Z の発注（AAPL の利確）

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class Broker : IBrokerAdapter
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public Dictionary<string, BrokerOrder?> Orders { get; } = [];

        public HashSet<string> Throws { get; } = [];

        public List<string> Queried { get; } = [];

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default)
        {
            Queried.Add(orderId);
            if (Throws.Contains(orderId))
                throw new InvalidOperationException("照会の故障注入");
            return Task.FromResult(Orders.TryGetValue(orderId, out var o) ? o : null);
        }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new InvalidOperationException("照会し直しは発注しない");

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) =>
            throw new InvalidOperationException("照会し直しは取り消さない（取消と置き直しはしない＝裁定）");
    }

    private static readonly OrderIntent CloseIntent =
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate, 13, 347.93m,
            PositionEffect.Close);

    private static ExecutionRecord Close(
        string orderId,
        DateTimeOffset at,
        OrderApprovalOrigin? origin = OrderApprovalOrigin.TradeDecision,
        OrderStatus status = OrderStatus.Accepted,
        PositionEffect effect = PositionEffect.Close,
        int filled = 0,
        Guid? decisionId = null) =>
        new(decisionId ?? Guid.NewGuid(), orderId, "AAPL", Market.UnitedStates,
            effect == PositionEffect.Close ? TradeSide.Sell : TradeSide.Buy, ProductType.Cash, effect, 13, 347.93m,
            filled, filled > 0 ? 347.93m : 0m, status, 0m, at, ApprovalOrigin: origin);

    private static BrokerOrder Snapshot(string orderId, OrderStatus status, int filled = 0) =>
        new(orderId, CloseIntent, status, filled, filled > 0 ? 347.93m : 0m, PlacedAt: default,
            CompletedAt: OrderStatusLifecycle.IsTerminal(status) ? Now : null);

    // 打ち切りまで済ませた記録を作る（通常の巡回で打ち切り → 印を書く。Worker と同じ順）。
    private static async Task<(InMemoryExecutedOrderStore Store, Broker Broker, OrderFillPoller Poller)> AbandonedAsync(
        params ExecutionRecord[] records)
    {
        var store = new InMemoryExecutedOrderStore();
        var broker = new Broker();
        var poller = new OrderFillPoller(broker, store, new FixedClock(Now));
        foreach (var r in records)
            store.Save(r);

        var polled = await poller.PollOnceAsync(MaxTracking, batchSize: 100);
        foreach (var a in polled.Abandoned!)
            store.MarkTrackingAbandoned(a.OrderId, a.TrackedFrom).Should().BeTrue();
        broker.Queried.Clear();
        return (store, broker, poller);
    }

    // T-10-2654（陽性・P−）: 追跡を打ち切った判断の決済を、ブローカーが失効（Rejected へ写る）・取消・約定と答えたら解放する。
    // 記録は終端になり、OrderExecuted を 1 回出す（リスク管理の台帳の終端・手仕舞いの未約定残の通知が届く）。
    [Theory]
    [InlineData(OrderStatus.Rejected, 0)]
    [InlineData(OrderStatus.Cancelled, 0)]
    [InlineData(OrderStatus.Expired, 0)]
    [InlineData(OrderStatus.Cancelled, 5)]
    [InlineData(OrderStatus.Filled, 13)]
    public async Task T_10_2654_打ち切った決済はブローカーが終端と答えたら解放する(OrderStatus terminal, int filled)
    {
        var decisionId = Guid.NewGuid();
        var (store, broker, poller) = await AbandonedAsync(Close("7860264464660206071", PlacedAt, decisionId: decisionId));
        store.FindAbandonedApprovedCloses(100).Should().ContainSingle("前提: 追跡を打ち切った決済が 1 件ある");
        broker.Orders["7860264464660206071"] = Snapshot("7860264464660206071", terminal, filled);

        var result = await poller.RecheckAbandonedClosesAsync(batchSize: 100);

        result.Scanned.Should().Be(1);
        var released = result.Released.Should().ContainSingle().Subject;
        released.Record.DecisionId.Should().Be(decisionId);
        released.TerminalStatus.Should().Be(terminal);
        released.FilledQuantity.Should().Be(filled);
        result.Executed.Should().ContainSingle(e =>
            e.DecisionId == decisionId && e.OrderId == "7860264464660206071" && e.Status == terminal
            && e.FilledQuantity == filled && e.Provider == BrokerProvider.MoomooSimulate);
        store.FindByDecisionId(decisionId)!.Status.Should().Be(terminal, "記録を終端にする（解放）");
        store.FindPendingCloses("AAPL", Market.UnitedStates, TradeSide.Sell).Should().BeEmpty(
            "処理中の決済として数えられなくなる");

        // 解放した決済は二度と照会しない。
        broker.Queried.Clear();
        (await poller.RecheckAbandonedClosesAsync(batchSize: 100)).Scanned.Should().Be(0);
        broker.Queried.Should().BeEmpty();
    }

    // T-10-2655（陰性・P+・P0）: 🔴 ブローカーが生きている（Accepted）・一部約定・不明（null）・例外と答えたら解放しない。
    // 一部約定は進捗（約定数）だけを記録し、非終端のまま照会し直しを続ける。
    [Theory]
    [InlineData("accepted")]
    [InlineData("partial")]
    [InlineData("null")]
    [InlineData("throws")]
    public async Task T_10_2655_非終端や不明や例外なら解放しない(string answer)
    {
        var decisionId = Guid.NewGuid();
        var (store, broker, poller) = await AbandonedAsync(Close("ORD-1", PlacedAt, decisionId: decisionId));
        switch (answer)
        {
            case "accepted":
                broker.Orders["ORD-1"] = Snapshot("ORD-1", OrderStatus.Accepted);
                break;
            case "partial":
                broker.Orders["ORD-1"] = Snapshot("ORD-1", OrderStatus.PartiallyFilled, filled: 5);
                break;
            case "throws":
                broker.Throws.Add("ORD-1");
                break;
        }

        var result = await poller.RecheckAbandonedClosesAsync(batchSize: 100);

        result.Scanned.Should().Be(1);
        result.Released.Should().BeEmpty("ブローカーの終端を確かめられないものは解放しない");
        result.Executed.Where(e => OrderStatusLifecycle.IsTerminal(e.Status)).Should().BeEmpty();
        var record = store.FindByDecisionId(decisionId)!;
        OrderStatusLifecycle.IsPending(record.Status).Should().BeTrue();
        store.FindPendingCloses("AAPL", Market.UnitedStates, TradeSide.Sell).Should().ContainSingle(
            "処理中の決済の記録は残る（数えるかはゲートがブローカーへ確かめる）");
        store.FindAbandonedApprovedCloses(100).Should().ContainSingle("次の間隔でも照会し直す");
        if (answer == "partial")
        {
            record.FilledQuantity.Should().Be(5, "一部約定の進捗は記録する");
            result.Executed.Should().ContainSingle(e => e.Status == OrderStatus.PartiallyFilled && e.FilledQuantity == 5);
            result.StillPending.Should().Be(1);
        }
        else if (answer == "accepted")
        {
            result.StillPending.Should().Be(1);
        }
        else
        {
            result.Unknown.Should().Be(1);
        }
    }

    // T-10-2656（境界）: 照会し直すのは「追跡を打ち切った・非終端・承認経路（出どころあり）の決済」だけである。
    // 追跡中（期限の内）・打ち切ったが印の無いもの（印を書く前）・出どころの無い決済（S1 の決済・保護レグ）・新規建て・終端済みは照会しない。
    [Fact]
    public async Task T_10_2656_照会し直す対象は打ち切った承認経路の非終端の決済だけ()
    {
        var store = new InMemoryExecutedOrderStore();
        var broker = new Broker();
        var poller = new OrderFillPoller(broker, store, new FixedClock(Now));
        store.Save(Close("decision", PlacedAt, OrderApprovalOrigin.TradeDecision));
        store.Save(Close("owner", PlacedAt.AddMinutes(1), OrderApprovalOrigin.OwnerClose));
        store.Save(Close("unknown-origin", PlacedAt.AddMinutes(2), OrderApprovalOrigin.Unknown));
        store.Save(Close("s1-leg", PlacedAt.AddMinutes(3), origin: null));
        store.Save(Close("open", PlacedAt.AddMinutes(4), effect: PositionEffect.Open));
        store.Save(Close("within-window", Now.AddHours(-23.9)));
        store.Save(Close("no-mark", PlacedAt.AddMinutes(5)));
        store.Save(Close("filled", PlacedAt.AddMinutes(6), status: OrderStatus.Filled, filled: 13));
        foreach (var id in new[] { "decision", "owner", "unknown-origin", "s1-leg", "open", "filled" })
            store.MarkTrackingAbandoned(id, store.GetAll().Single(r => r.OrderId == id).ExecutedAt).Should().BeTrue();

        var result = await poller.RecheckAbandonedClosesAsync(batchSize: 100);

        broker.Queried.Should().Equal(["decision", "owner", "unknown-origin"],
            "出どころが分からない（Unknown）承認も承認経路として照会し直す。出どころの無い記録（null）は保護の機構のレグ");
        result.Scanned.Should().Be(3);
        result.Unknown.Should().Be(3, "照会はすべて null（解放しない）");

        // 上限は古い順に効く。
        broker.Queried.Clear();
        (await poller.RecheckAbandonedClosesAsync(batchSize: 1)).Scanned.Should().Be(1);
        broker.Queried.Should().Equal(["decision"]);
    }

    public static TheoryData<string> Implementations() => ["ef", "inmemory"];

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

    // T-10-2659: 問い合わせ 2 つ（打ち切った承認経路の決済・発注から一定時間たった承認経路の決済）の意味論を、
    // 本番の DB 実装とインメモリ実装で同一に固定する（片側だけの乖離を検知する）。
    [Theory]
    [MemberData(nameof(Implementations))]
    public void T_10_2659_問い合わせの意味論をDBとインメモリで同一に固定する(string kind)
    {
        var store = Stores(kind);
        store().Save(Close("abandoned", PlacedAt));
        store().Save(Close("abandoned-partial", PlacedAt.AddMinutes(1), status: OrderStatus.PartiallyFilled, filled: 5));
        store().Save(Close("abandoned-renewed", PlacedAt.AddMinutes(2)));
        store().Save(Close("abandoned-s1", PlacedAt.AddMinutes(3), origin: null));
        store().Save(Close("abandoned-open", PlacedAt.AddMinutes(4), effect: PositionEffect.Open));
        store().Save(Close("abandoned-expired", PlacedAt.AddMinutes(5), status: OrderStatus.Expired));
        store().Save(Close("fresh", Now.AddMinutes(-20)));
        store().Save(Close("just-placed", Now.AddMinutes(-5)));
        foreach (var r in store().GetAll().Where(r => r.OrderId.StartsWith("abandoned", StringComparison.Ordinal)))
            store().MarkTrackingAbandoned(r.OrderId, r.ExecutedAt).Should().BeTrue();
        // 起点が進んだ（別の追跡になった）記録は、打ち切った決済に数えない。
        store().RenewTracking("abandoned-renewed", Now.AddHours(-1)).Should().BeTrue();

        store().FindAbandonedApprovedCloses(100).Select(r => r.OrderId)
            .Should().Equal(["abandoned", "abandoned-partial"]);
        store().FindAbandonedApprovedCloses(1).Select(r => r.OrderId).Should().Equal(["abandoned"]);

        // 発注から 15 分以上たった非終端の承認経路の決済（打ち切りの有無を問わない）。境界: ちょうど 15 分前は含まない。
        store().Save(Close("boundary", Now.AddMinutes(-15)));
        store().FindStaleApprovedCloses(Now.AddMinutes(-15), 100).Select(r => r.OrderId)
            .Should().Equal(["abandoned", "abandoned-partial", "abandoned-renewed", "fresh"]);
        store().FindStaleApprovedCloses(Now.AddMinutes(-15), 2).Select(r => r.OrderId)
            .Should().Equal(["abandoned", "abandoned-partial"]);

        // 解放（終端化）した記録は、どちらの問い合わせからも消える。
        store().UpdateOutcome("abandoned", OrderStatus.Rejected, 0, 0m, 0m, Now).Should().BeTrue();
        store().FindAbandonedApprovedCloses(100).Select(r => r.OrderId).Should().Equal(["abandoned-partial"]);
        store().FindStaleApprovedCloses(Now.AddMinutes(-15), 100).Select(r => r.OrderId)
            .Should().NotContain("abandoned");
    }
}
