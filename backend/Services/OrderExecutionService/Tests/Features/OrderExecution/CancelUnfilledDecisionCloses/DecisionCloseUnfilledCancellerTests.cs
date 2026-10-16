using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.AmendOrder;
using OrderExecutionService.Features.OrderExecution.CancelUnfilledDecisionCloses;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 FR-10, FR-19, FR-05, ADR-0067 決定1〜4, 05_trading-assumptions §5, #1339, IADR-0539:
// 判断由来の決済の指値が発注から N 分未約定なら系が取り消すだけにする（既定は無効・1 建玉 1 日 1 回）。
// 作業仕様書 20261016_1339_decision-close-unfilled-cancel §受け入れ基準 A1〜A6・A8。
public class DecisionCloseUnfilledCancellerTests
{
    // 2026-10-14 14:00Z = 米国東部 10:00（EDT）。
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 14, 0, 0, TimeSpan.Zero);

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    // 取消を受けると（既定で）その注文を Cancelled にする。照会は記録した状態を返す。
    private sealed class Broker : IBrokerAdapter
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public Dictionary<string, BrokerOrder?> Orders { get; } = [];

        public List<string> Cancelled { get; } = [];

        public List<OrderIntent> Placed { get; } = [];

        public HashSet<string> CancelThrows { get; } = [];

        public HashSet<string> CancelStaysAlive { get; } = [];

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult(Orders.TryGetValue(orderId, out var o) ? o : null);

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            // 🔴 決定1: 置き直し・成行への切り替えはしない。呼ばれたら試験が落ちる。
            Placed.Add(intent);
            throw new InvalidOperationException("取消の後に発注してはならない（置き直し・成行への切り替えはしない）");
        }

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            Cancelled.Add(orderId);
            if (CancelThrows.Contains(orderId))
                throw new InvalidOperationException("取消の故障注入");
            if (!CancelStaysAlive.Contains(orderId) && Orders.TryGetValue(orderId, out var o) && o is not null)
                Orders[orderId] = o with { Status = OrderStatus.Cancelled, CompletedAt = Now };
            return Task.CompletedTask;
        }
    }

    private static readonly OrderIntent CloseIntent =
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate, 13, 347.93m,
            PositionEffect.Close);

    private static ExecutionRecord Close(
        string orderId,
        DateTimeOffset placedAt,
        OrderApprovalOrigin? origin = OrderApprovalOrigin.TradeDecision,
        OrderStatus status = OrderStatus.Accepted,
        int filled = 0,
        string symbol = "AAPL",
        PositionEffect effect = PositionEffect.Close,
        Market market = Market.UnitedStates) =>
        new(Guid.NewGuid(), orderId, symbol, market, effect == PositionEffect.Close ? TradeSide.Sell : TradeSide.Buy,
            ProductType.Cash, effect, 13, 347.93m, filled, filled > 0 ? 347.93m : 0m, status, 0m, placedAt, ApprovalOrigin: origin);

    private static DecisionCloseUnfilledCancelOptions Enabled(int minutes = 15, int max = 1) =>
        new() { Enabled = true, UnfilledMinutes = minutes, MaxCancelsPerPositionPerDay = max };

    private sealed record Fixture(
        IExecutedOrderStore Store, Broker Broker, MutableClock Clock, DecisionCloseUnfilledCanceller Canceller,
        InMemoryOrderLifecycleStore Lifecycle);

    private static Fixture Build(DecisionCloseUnfilledCancelOptions options, IExecutedOrderStore? store = null, params ExecutionRecord[] records)
    {
        store ??= new InMemoryExecutedOrderStore();
        var broker = new Broker();
        foreach (var r in records)
        {
            store.Save(r);
            broker.Orders[r.OrderId] = new BrokerOrder(
                r.OrderId, CloseIntent with { Symbol = r.Symbol }, r.Status, r.FilledQuantity, r.AveragePrice, r.ExecutedAt, null);
        }

        var clock = new MutableClock(Now);
        var lifecycle = new InMemoryOrderLifecycleStore();
        var amendments = new OrderAmendmentService(broker, store, lifecycle, clock);
        return new Fixture(store, broker, clock,
            new DecisionCloseUnfilledCanceller(store, amendments, clock, Options.Create(options)), lifecycle);
    }

    // T-10-2670: 無効（既定）なら何もしない。ストアを読まず、取消を送らない（決定2）。
    [Fact]
    public async Task T_10_2670_無効なら何もしない()
    {
        new DecisionCloseUnfilledCancelOptions().Enabled.Should().BeFalse("ADR-0067 決定2: 既定は無効");

        var store = new ReadCountingStore();
        var f = Build(new DecisionCloseUnfilledCancelOptions(), store, Close("ORD-1", Now.AddHours(-3)));
        store.Reads = 0;

        var result = await f.Canceller.RunOnceAsync();

        result.Should().BeSameAs(DecisionCloseUnfilledCancelResult.Empty);
        f.Broker.Cancelled.Should().BeEmpty();
        store.Reads.Should().Be(0, "無効ならストアも読まない");
        f.Store.FindByDecisionId(f.Store.GetAll()[0].DecisionId)!.UnfilledCancelRequestedAt.Should().BeNull();
    }

    // T-10-2671: 窓の両端（発注から N 分以上＝前の端・まだ非終端＝後の端）。N 分未満・終端は取り消さない。一部約定は取り消す。
    // 置き直し・成行は送らない。確認できた取消は OrderCancelled（相場操縦の検知の母集団へ入る）。
    [Fact]
    public async Task T_10_2671_発注からN分たって非終端の判断由来の決済だけを取り消す()
    {
        var f = Build(Enabled(),
            records:
            [
                Close("ORD-STALE", Now.AddMinutes(-15)),          // ちょうど N 分（含む）
                Close("ORD-FRESH", Now.AddMinutes(-14), symbol: "MSFT"),
                Close("ORD-FILLED", Now.AddMinutes(-40), status: OrderStatus.Filled, filled: 13, symbol: "NVDA"),
                Close("ORD-EXPIRED", Now.AddMinutes(-40), status: OrderStatus.Expired, symbol: "AMZN"),
                Close("ORD-PARTIAL", Now.AddMinutes(-30), status: OrderStatus.PartiallyFilled, filled: 5, symbol: "TSLA"),
            ]);

        var result = await f.Canceller.RunOnceAsync();

        f.Broker.Cancelled.Should().BeEquivalentTo(["ORD-PARTIAL", "ORD-STALE"]);
        f.Broker.Placed.Should().BeEmpty("置き直し・成行への切り替えはしない（決定1）");
        result.Requested.Select(r => r.OrderId).Should().Equal(["ORD-PARTIAL", "ORD-STALE"]);
        result.Confirmed.Select(c => c.OrderId).Should().BeEquivalentTo(["ORD-PARTIAL", "ORD-STALE"]);
        result.Confirmed.Single(c => c.OrderId == "ORD-PARTIAL").ObservedFilledQuantity.Should().Be(5, "一部約定は残りだけを取り消す");
        result.Confirmed.Should().OnlyContain(c => c.Reason.StartsWith(DecisionCloseUnfilledCanceller.ReasonPrefix, StringComparison.Ordinal));
        f.Lifecycle.GetByOrderId("ORD-STALE").Should().ContainSingle(e => e.Kind == OrderLifecycleKind.Cancelled);
        f.Lifecycle.GetByOrderId("ORD-PARTIAL").Should().ContainSingle(e => e.Kind == OrderLifecycleKind.Cancelled);

        // 記録の状態は書き換えない（解放は約定追跡が終端を確かめてから。IADR-0536 決定3）。
        f.Store.GetAll().Single(r => r.OrderId == "ORD-STALE").Status.Should().Be(OrderStatus.Accepted);
        f.Store.GetAll().Single(r => r.OrderId == "ORD-STALE").UnfilledCancelRequestedAt.Should().Be(Now);
        f.Store.GetAll().Single(r => r.OrderId == "ORD-FRESH").UnfilledCancelRequestedAt.Should().BeNull();

        // N 分を過ぎたら、まだ非終端の ORD-FRESH を取り消す。同じ注文は二度取り消さない。
        f.Clock.UtcNow = Now.AddMinutes(1);
        await f.Canceller.RunOnceAsync();
        f.Broker.Cancelled.Should().BeEquivalentTo(["ORD-PARTIAL", "ORD-STALE", "ORD-FRESH"]);
    }

    // T-10-2672: 1 建玉 1 日 1 回（市場の現地取引日。米国株は米国東部の暦日）。上限に達したら取り消さず DAY の失効に任せる。
    // D+（同じ現地取引日の 2 本目）・Dz（UTC では翌日だが米国東部では同じ日）・D−（翌現地取引日）を突き合わせる。
    [Fact]
    public async Task T_10_2672_一建玉一日一回までで市場の現地取引日で数える()
    {
        // 1 本目: 10-14 19:00Z（米国東部 15:00）に取り消す。
        var f = Build(Enabled(), records: [Close("ORD-1", new DateTimeOffset(2026, 10, 14, 18, 30, 0, TimeSpan.Zero))]);
        f.Clock.UtcNow = new DateTimeOffset(2026, 10, 14, 19, 0, 0, TimeSpan.Zero);
        (await f.Canceller.RunOnceAsync()).Requested.Should().ContainSingle();

        // 同じ建玉の 2 本目（同じ現地取引日）: 取り消さない。別の建玉（MSFT）は数えない。
        Save(f, Close("ORD-2", new DateTimeOffset(2026, 10, 14, 19, 10, 0, TimeSpan.Zero)));
        Save(f, Close("ORD-MSFT", new DateTimeOffset(2026, 10, 14, 19, 10, 0, TimeSpan.Zero), symbol: "MSFT"));
        f.Clock.UtcNow = new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero);
        var same = await f.Canceller.RunOnceAsync();
        same.Capped.Select(r => r.OrderId).Should().Equal(["ORD-2"], "D+: 同じ現地取引日の 2 本目は取り消さない");
        same.Requested.Select(r => r.OrderId).Should().Equal(["ORD-MSFT"], "上限は建玉ごと");

        // Dz: 10-15 02:00Z は UTC では翌日だが、米国東部では 10-14 22:00（同じ取引日）。取り消さない。
        f.Clock.UtcNow = new DateTimeOffset(2026, 10, 15, 2, 0, 0, TimeSpan.Zero);
        (await f.Canceller.RunOnceAsync()).Capped.Select(r => r.OrderId)
            .Should().Equal(["ORD-2"], "Dz: UTC の暦日ではなく市場の現地取引日で数える");

        // D−: 10-15 14:00Z（米国東部 10-15 10:00）は翌取引日。1 本目から 24 時間たっていないが取り消せる。
        f.Clock.UtcNow = new DateTimeOffset(2026, 10, 15, 14, 0, 0, TimeSpan.Zero);
        (await f.Canceller.RunOnceAsync()).Requested.Select(r => r.OrderId)
            .Should().Equal(["ORD-2"], "D−: 翌現地取引日は改めて 1 回まで（過去 24 時間では数えない）");
        f.Broker.Cancelled.Should().Equal(["ORD-1", "ORD-MSFT", "ORD-2"]);

        // 上限を 2 にすると同じ日の 2 本目も取り消す（構成の上限が効く）。
        var g = Build(Enabled(max: 2), records:
        [
            Close("A-1", Now.AddMinutes(-30)),
            Close("A-2", Now.AddMinutes(-20)),
            Close("A-3", Now.AddMinutes(-16)),
        ]);
        (await g.Canceller.RunOnceAsync()).Requested.Select(r => r.OrderId).Should().Equal(["A-1", "A-2"]);
    }

    // 市場の現地取引日の範囲（サマータイムは TimeZoneInfo が吸収する）。
    [Fact]
    public void T_10_2672_市場の現地取引日の範囲はUTCの暦日ではない()
    {
        DecisionCloseUnfilledCanceller.TradingDayRange(Market.UnitedStates, new DateTimeOffset(2026, 10, 15, 2, 0, 0, TimeSpan.Zero))
            .Should().Be((new DateTimeOffset(2026, 10, 14, 4, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 15, 4, 0, 0, TimeSpan.Zero)));
        // 冬時間（EST・UTC-5）。
        DecisionCloseUnfilledCanceller.TradingDayRange(Market.UnitedStates, new DateTimeOffset(2026, 12, 1, 15, 0, 0, TimeSpan.Zero))
            .Should().Be((new DateTimeOffset(2026, 12, 1, 5, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 2, 5, 0, 0, TimeSpan.Zero)));
        // 日本株は JST の暦日。
        DecisionCloseUnfilledCanceller.TradingDayRange(Market.Japan, new DateTimeOffset(2026, 10, 14, 16, 0, 0, TimeSpan.Zero))
            .Should().Be((new DateTimeOffset(2026, 10, 14, 15, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 15, 15, 0, 0, TimeSpan.Zero)));
    }

    // T-10-2673: 適用は判断由来の決済だけ（決定4）。利用者の手仕舞い・維持率割れの自動縮小・出どころ不明・出どころなし・新規建ては取り消さない。
    [Fact]
    public async Task T_10_2673_判断由来の決済以外は取り消さない()
    {
        var f = Build(Enabled(), records:
        [
            Close("OWNER", Now.AddHours(-1), origin: OrderApprovalOrigin.OwnerClose),
            Close("MARGIN", Now.AddHours(-1), origin: OrderApprovalOrigin.MaintenanceMarginReduction),
            Close("UNKNOWN", Now.AddHours(-1), origin: OrderApprovalOrigin.Unknown),
            Close("S1", Now.AddHours(-1), origin: null),
            Close("OPEN", Now.AddHours(-1), effect: PositionEffect.Open),
            Close("DECISION", Now.AddHours(-1), symbol: "MSFT"),
        ]);

        var result = await f.Canceller.RunOnceAsync();

        f.Broker.Cancelled.Should().Equal(["DECISION"]);
        result.Requested.Select(r => r.OrderId).Should().Equal(["DECISION"]);
    }

    // T-10-2674: 印を先に書く。取消が例外でも印は残り、二度と取り消さない（取消の反復を作らない）。回数にも数える。
    // 取り消せたと確認できない（まだ生きている）ときは OrderCancelled を出さない（解放は約定追跡が終端を確かめてから）。
    [Fact]
    public async Task T_10_2674_印を先に書き取消の失敗でも二度取り消さない()
    {
        var f = Build(Enabled(), records:
        [
            Close("THROWS", Now.AddMinutes(-20)),
            Close("ALIVE", Now.AddMinutes(-20), symbol: "MSFT"),
        ]);
        f.Broker.CancelThrows.Add("THROWS");
        f.Broker.CancelStaysAlive.Add("ALIVE");

        var first = await f.Canceller.RunOnceAsync();

        first.Failed.Should().Be(1);
        first.Confirmed.Should().BeEmpty("取り消せたと確認できない取消は OrderCancelled にしない（在庫の押さえを解かない）");
        f.Store.GetAll().Should().OnlyContain(r => r.UnfilledCancelRequestedAt == Now, "印は取消の前に書く");

        // 次の巡回: 同じ注文を二度取り消さない。回数にも数えたので、同じ建玉の新しい決済も同じ日は取り消さない。
        Save(f, Close("THROWS-2", Now.AddMinutes(-16)));
        f.Clock.UtcNow = Now.AddMinutes(5);
        var second = await f.Canceller.RunOnceAsync();
        f.Broker.Cancelled.Should().Equal(["THROWS", "ALIVE"]);
        second.Capped.Select(r => r.OrderId).Should().Equal(["THROWS-2"]);
    }

    // T-10-2675: 有効時の N は相場操縦の検知の窓（5 分）より長い（決定2）。N ≦ 窓・上端超え・回数の範囲外は構成エラー。無効なら検証しない。
    [Theory]
    [InlineData(true, 5, 1, false)]   // N ＝ 窓 → エラー（窓より長くない）
    [InlineData(true, 4, 1, false)]
    [InlineData(true, 6, 1, true)]    // 窓 ＋ 1 分 → 可
    [InlineData(true, 15, 1, true)]   // 既定
    [InlineData(true, 390, 3, true)]  // 上端
    [InlineData(true, 391, 1, false)] // 上端超え
    [InlineData(true, 15, 0, false)]  // 回数 0
    [InlineData(true, 15, 4, false)]  // 回数 4
    [InlineData(false, 5, 0, true)]   // 無効なら検証しない
    public void T_10_2675_有効時のNは検知の窓より長い(bool enabled, int minutes, int max, bool valid)
    {
        var options = new DecisionCloseUnfilledCancelOptions
        {
            Enabled = enabled,
            UnfilledMinutes = minutes,
            MaxCancelsPerPositionPerDay = max,
        };

        options.Validate().Should().HaveCount(valid ? 0 : 1);
        new DecisionCloseUnfilledCancelOptionsValidator().Validate(null, options).Succeeded.Should().Be(valid);
        DecisionCloseUnfilledCancelDefaults.ManipulationLookbackWindowMinutes.Should().Be(5, "IADR-0040 の検知の窓");
    }

    // T-10-2677: 対象の問い合わせ・印・回数の数え方を DB とインメモリで同一に固定する（片側だけの乖離を検知する）。
    public static TheoryData<string> Implementations() => ["ef", "inmemory"];

    [Theory]
    [MemberData(nameof(Implementations))]
    public void T_10_2677_問い合わせの意味論をDBとインメモリで同一に固定する(string kind)
    {
        var store = Stores(kind);
        store().Save(Close("old", Now.AddMinutes(-40)));
        store().Save(Close("partial", Now.AddMinutes(-30), status: OrderStatus.PartiallyFilled, filled: 5));
        store().Save(Close("boundary", Now.AddMinutes(-15)));
        store().Save(Close("fresh", Now.AddMinutes(-14)));
        store().Save(Close("filled", Now.AddMinutes(-40), status: OrderStatus.Filled, filled: 13));
        store().Save(Close("owner", Now.AddMinutes(-40), origin: OrderApprovalOrigin.OwnerClose));
        store().Save(Close("unknown", Now.AddMinutes(-40), origin: OrderApprovalOrigin.Unknown));
        store().Save(Close("s1", Now.AddMinutes(-40), origin: null));
        store().Save(Close("open", Now.AddMinutes(-40), effect: PositionEffect.Open));

        store().FindUnfilledDecisionCloses(Now.AddMinutes(-15), 100).Select(r => r.OrderId)
            .Should().Equal(["old", "partial", "boundary"]);
        store().FindUnfilledDecisionCloses(Now.AddMinutes(-15), 1).Select(r => r.OrderId).Should().Equal(["old"]);

        // 印: 非終端・未印だけに書ける。書いたら対象から消える。
        store().MarkUnfilledCancelRequested("old", Now).Should().BeTrue();
        store().MarkUnfilledCancelRequested("old", Now.AddMinutes(1)).Should().BeFalse("既に印がある");
        store().MarkUnfilledCancelRequested("filled", Now).Should().BeFalse("終端");
        store().MarkUnfilledCancelRequested("missing", Now).Should().BeFalse("記録が無い");
        store().FindUnfilledDecisionCloses(Now.AddMinutes(-15), 100).Select(r => r.OrderId)
            .Should().Equal(["partial", "boundary"]);
        store().FindByDecisionId(store().GetAll().Single(r => r.OrderId == "old").DecisionId)!
            .UnfilledCancelRequestedAt.Should().Be(Now);

        // 回数: 同じ建玉・範囲 [from, to)。取り消した後に終端しても数える。
        store().UpdateOutcome("old", OrderStatus.Cancelled, 0, 0m, 0m, Now).Should().BeTrue();
        store().CountUnfilledCancelRequests("AAPL", Market.UnitedStates, TradeSide.Sell, Now, Now.AddDays(1)).Should().Be(1);
        store().CountUnfilledCancelRequests("AAPL", Market.UnitedStates, TradeSide.Sell, Now.AddTicks(1), Now.AddDays(1)).Should().Be(0);
        store().CountUnfilledCancelRequests("AAPL", Market.UnitedStates, TradeSide.Sell, Now.AddDays(-1), Now).Should().Be(0, "終わりは含まない");
        store().CountUnfilledCancelRequests("MSFT", Market.UnitedStates, TradeSide.Sell, Now, Now.AddDays(1)).Should().Be(0);
        store().CountUnfilledCancelRequests("AAPL", Market.UnitedStates, TradeSide.Buy, Now, Now.AddDays(1)).Should().Be(0);
    }

    private static void Save(Fixture f, ExecutionRecord r)
    {
        f.Store.Save(r);
        f.Broker.Orders[r.OrderId] = new BrokerOrder(
            r.OrderId, CloseIntent with { Symbol = r.Symbol }, r.Status, r.FilledQuantity, r.AveragePrice, r.ExecutedAt, null);
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

    // 読み取りの回数を数える包み型（無効時にストアを読まないことの確認）。
    private sealed class ReadCountingStore : IExecutedOrderStore
    {
        private readonly InMemoryExecutedOrderStore _inner = new();

        public int Reads { get; set; }

        public void Save(ExecutionRecord record) => _inner.Save(record);

        public IReadOnlyList<ExecutionRecord> GetAll()
        {
            Reads++;
            return _inner.GetAll();
        }

        public ExecutionRecord? FindByDecisionId(Guid decisionId) => _inner.FindByDecisionId(decisionId);

        public IReadOnlyList<ExecutionRecord> FindPendingSince(DateTimeOffset since, int batchSize) =>
            _inner.FindPendingSince(since, batchSize);

        public IReadOnlyList<ExecutionRecord> FindPendingByOrderIds(IReadOnlyCollection<string> orderIds) =>
            _inner.FindPendingByOrderIds(orderIds);

        public IReadOnlyList<ExecutionRecord> FindUnfilledDecisionCloses(DateTimeOffset placedAtOrBefore, int limit)
        {
            Reads++;
            return _inner.FindUnfilledDecisionCloses(placedAtOrBefore, limit);
        }

        public bool RenewTracking(string orderId, DateTimeOffset trackedFrom) => _inner.RenewTracking(orderId, trackedFrom);

        public bool UpdateOutcome(
            string orderId, OrderStatus status, int filledQuantity, decimal averagePrice, decimal slippageRatio, DateTimeOffset executedAt) =>
            _inner.UpdateOutcome(orderId, status, filledQuantity, averagePrice, slippageRatio, executedAt);
    }
}
