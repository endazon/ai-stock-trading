using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// FR-10, UC-02, ADR-0016 決定2(b), #331, IADR-0210: 保護逆指値の同時発注と「逆指値なしの建玉を持たない」
// （未受理時の建玉解消/不成立の全分岐）の検証。issue #331 受け入れ基準 1 のテスト群。
// 統制系の 3 点セット: 境界値テーブル（分岐表）＋プロパティベース（不変条件）＋否定形（Close は逆指値を張らない等）。
public class OrderExecutionServiceProtectiveStopTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 6, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    public enum StopBehavior { Accept, Reject, Throw, Unavailable }

    public enum RemedyBehavior { Succeed, Throw }

    // エントリー・逆指値・取消・成行手仕舞いの挙動を分岐単位で注入できるブローカ。
    private sealed class ScriptedBroker : IBrokerAdapter, IProtectiveOrderBroker
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public OrderStatus EntryStatus { get; init; } = OrderStatus.Accepted;
        public int EntryFilled { get; init; }
        public StopBehavior Stop { get; init; } = StopBehavior.Accept;
        public RemedyBehavior Cancel { get; init; } = RemedyBehavior.Succeed;
        public RemedyBehavior MarketClose { get; init; } = RemedyBehavior.Succeed;

        /// <summary>取消失敗後の再照会が返す約定数（null＝照会も失敗）。</summary>
        public int? RequeryFilled { get; init; }

        public int StopPlaceCount { get; private set; }
        public int CancelCount { get; private set; }
        public int MarketCloseCount { get; private set; }
        public OrderIntent? MarketCloseIntent { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            Task.FromResult(new BrokerOrder(
                "entry-1", intent, EntryStatus, EntryFilled, EntryFilled > 0 ? intent.Price : 0m, Now,
                CompletedAt: EntryStatus == OrderStatus.Filled ? Now : null));

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default)
        {
            StopPlaceCount++;
            return Stop switch
            {
                StopBehavior.Accept => Task.FromResult(new BrokerOrder(
                    "stop-1", closeIntent, OrderStatus.Accepted, 0, 0m, Now, null)),
                StopBehavior.Reject => Task.FromResult(new BrokerOrder(
                    "stop-1", closeIntent, OrderStatus.Rejected, 0, 0m, Now, Now)),
                StopBehavior.Unavailable => throw new BrokerUnavailableException("OpenD 切断（テスト）"),
                _ => throw new InvalidOperationException("逆指値の発注に失敗（テスト）"),
            };
        }

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            MarketCloseCount++;
            MarketCloseIntent = closeIntent;
            return MarketClose == RemedyBehavior.Succeed
                ? Task.FromResult(new BrokerOrder(
                    "close-1", closeIntent, OrderStatus.Filled, closeIntent.Quantity, closeIntent.Price, Now, Now))
                : throw new InvalidOperationException("成行手仕舞いに失敗（テスト）");
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(RequeryFilled is { } filled
                ? new BrokerOrder("entry-1", Intent(), OrderStatus.PartiallyFilled, filled, 1_000m, Now, null)
                : null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            CancelCount++;
            return Cancel == RemedyBehavior.Succeed
                ? Task.CompletedTask
                : throw new InvalidOperationException("取消に失敗（テスト）");
        }
    }

    private static OrderIntent Intent(int qty = 10, decimal? stopLoss = 950m, TradeSide side = TradeSide.Buy) =>
        new("AAPL", Market.UnitedStates, side, ProductType.Cash, BrokerProvider.MoomooSimulate, qty, 1_000m,
            PositionEffect.Open, stopLoss, FxRateToBase: 1m);

    private static (AppSvc Service, InMemoryExecutedOrderStore Store, InMemoryProtectiveStopOrderStore Stops,
        InMemoryOrderReservationStore Reservations) NewService(IBrokerAdapter broker)
    {
        var store = new InMemoryExecutedOrderStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        return (new AppSvc(broker, store, reservations, new FakeClock(), stops), store, stops, reservations);
    }

    private static OrderApproved Approved(OrderIntent intent) => new(Guid.NewGuid(), intent, intent.Quantity, Now);

    // ---- 同時発注（受理） ----

    [Theory]
    [InlineData(OrderStatus.Accepted, 0)]
    [InlineData(OrderStatus.PartiallyFilled, 4)]
    [InlineData(OrderStatus.Filled, 10)]
    public async Task エントリーが生きていれば保護逆指値が同時発注される(OrderStatus entryStatus, int filled)
    {
        var broker = new ScriptedBroker { EntryStatus = entryStatus, EntryFilled = filled };
        var (service, store, stops, _) = NewService(broker);
        var approved = Approved(Intent());

        var result = await service.ExecuteAsync(approved);

        broker.StopPlaceCount.Should().Be(1, "逆指値は建玉と同時に発注する（FR-10・ADR-0016 決定2(b)）");
        var placed = result.StopPlaced!;
        placed.EntryDecisionId.Should().Be(approved.DecisionId);
        placed.TriggerPrice.Should().Be(950m);
        placed.CloseIntent.Side.Should().Be(TradeSide.Sell);
        placed.CloseIntent.PositionEffect.Should().Be(PositionEffect.Close);
        placed.CloseIntent.Quantity.Should().Be(10);
        result.CoverageLost.Should().BeNull();

        // 逆指値レグは発注結果として記録され、約定追跡ポーリング（IADR-0113）の対象になる。
        store.FindByDecisionId(placed.StopDecisionId).Should().NotBeNull();
        stops.Find(approved.DecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }

    [Fact]
    public async Task ショートエントリーには買戻しの逆指値が張られる()
    {
        // ADR-0016 決定2(b): 逆指値の同時発注必須は建玉の方向を問わない。
        var broker = new ScriptedBroker { EntryStatus = OrderStatus.Filled, EntryFilled = 10 };
        var (service, _, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(Intent(side: TradeSide.Sell, stopLoss: 1_050m)));

        result.StopPlaced!.CloseIntent.Side.Should().Be(TradeSide.Buy, "ショートの決済は買戻し");
        result.StopPlaced.TriggerPrice.Should().Be(1_050m);
    }

    [Fact]
    public async Task 逆指値レグはエントリーの換算レートを引き継ぐ()
    {
        // FR-17, IADR-0107: 決済レグの FxRateToBase を落とすと外貨建て決済が未換算で台帳へ積まれる。
        var broker = new ScriptedBroker();
        var (service, _, _, _) = NewService(broker);
        var intent = Intent() with { Market = Market.Japan, FxRateToBase = 0.0068m };

        var result = await service.ExecuteAsync(new OrderApproved(Guid.NewGuid(), intent, intent.Quantity, Now));

        result.StopPlaced!.CloseIntent.FxRateToBase.Should().Be(0.0068m);
    }

    // ---- 未受理時の建玉解消（全分岐・境界値テーブル） ----

    [Fact]
    public async Task 逆指値未受理でエントリー未約定なら取り消す()
    {
        var broker = new ScriptedBroker { EntryStatus = OrderStatus.Accepted, EntryFilled = 0, Stop = StopBehavior.Reject };
        var (service, _, stops, _) = NewService(broker);
        var approved = Approved(Intent());

        var result = await service.ExecuteAsync(approved);

        broker.CancelCount.Should().Be(1, "未約定のエントリーは取り消す（業務フロー 02 の表）");
        broker.MarketCloseCount.Should().Be(0);
        var lost = result.CoverageLost!;
        lost.Cause.Should().Be(ProtectiveStopLossCause.RejectedAtEntry);
        lost.Remediation.Should().Be(ProtectiveStopRemediation.EntryCancelled);
        result.StopPlaced.Should().BeNull();
        stops.Find(approved.DecisionId).Should().BeNull("保護は成立していない");
    }

    [Theory]
    [InlineData(OrderStatus.Filled, 10, 10)]        // 全量約定 → 全量手仕舞い
    [InlineData(OrderStatus.PartiallyFilled, 4, 4)] // 部分約定 → 約定分だけ手仕舞い
    [InlineData(OrderStatus.PartiallyFilled, 1, 1)] // 境界: 最小の約定数
    public async Task 逆指値未受理でエントリー約定済みなら成行で手仕舞う(OrderStatus entryStatus, int filled, int expectedCloseQty)
    {
        var broker = new ScriptedBroker { EntryStatus = entryStatus, EntryFilled = filled, Stop = StopBehavior.Reject };
        var (service, store, _, _) = NewService(broker);
        var approved = Approved(Intent());

        var result = await service.ExecuteAsync(approved);

        broker.MarketCloseCount.Should().Be(1, "約定済みの建玉は即座に成行で手仕舞う（業務フロー 02 の表）");
        broker.MarketCloseIntent!.Quantity.Should().Be(expectedCloseQty);
        broker.MarketCloseIntent.Side.Should().Be(TradeSide.Sell);
        var lost = result.CoverageLost!;
        lost.Remediation.Should().Be(ProtectiveStopRemediation.PositionClosed);
        lost.Quantity.Should().Be(expectedCloseQty);
        lost.CloseDecisionId.Should().NotBeNull("手仕舞いレグは台帳へ結線される");
        store.FindByDecisionId(lost.CloseDecisionId!.Value).Should().NotBeNull();
    }

    [Fact]
    public async Task 取消失敗後に約定が判明したら約定分を手仕舞う()
    {
        // 取消と約定の競合: 取消が失敗＝その間に約定した可能性 → 再照会して約定分を手仕舞いへ回す。
        var broker = new ScriptedBroker
        {
            EntryStatus = OrderStatus.Accepted,
            EntryFilled = 0,
            Stop = StopBehavior.Reject,
            Cancel = RemedyBehavior.Throw,
            RequeryFilled = 6,
        };
        var (service, _, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(Intent()));

        broker.MarketCloseIntent!.Quantity.Should().Be(6);
        result.CoverageLost!.Remediation.Should().Be(ProtectiveStopRemediation.PositionClosed);
    }

    [Fact]
    public async Task 取消も照会も失敗したら人手対応のNoneを返す_否定形()
    {
        // 状態不明のまま自動で注文を重ねない（誤発注の方が危険）。None は Critical 通知で人手対応を求める。
        var broker = new ScriptedBroker
        {
            EntryStatus = OrderStatus.Accepted,
            EntryFilled = 0,
            Stop = StopBehavior.Reject,
            Cancel = RemedyBehavior.Throw,
            RequeryFilled = null,
        };
        var (service, _, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(Intent()));

        result.CoverageLost!.Remediation.Should().Be(ProtectiveStopRemediation.None);
        broker.MarketCloseCount.Should().Be(0, "状態不明のまま注文を重ねない");
    }

    [Fact]
    public async Task 手仕舞いも失敗したらNoneを返す()
    {
        var broker = new ScriptedBroker
        {
            EntryStatus = OrderStatus.Filled,
            EntryFilled = 10,
            Stop = StopBehavior.Reject,
            MarketClose = RemedyBehavior.Throw,
        };
        var (service, _, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(Intent()));

        result.CoverageLost!.Remediation.Should().Be(ProtectiveStopRemediation.None);
    }

    [Theory]
    [InlineData(StopBehavior.Throw)]
    [InlineData(StopBehavior.Unavailable)]
    public async Task 逆指値の発注例外も未受理と同じ分岐に入る(StopBehavior stop)
    {
        var broker = new ScriptedBroker { EntryStatus = OrderStatus.Filled, EntryFilled = 10, Stop = stop };
        var (service, _, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(Intent()));

        result.CoverageLost.Should().NotBeNull("逆指値を張れなかった建玉は持たない（fail-closed）");
        result.CoverageLost!.Remediation.Should().Be(ProtectiveStopRemediation.PositionClosed);
    }

    [Fact]
    public async Task エントリーが終端失敗なら保護レグを試みない()
    {
        var broker = new ScriptedBroker { EntryStatus = OrderStatus.Rejected, EntryFilled = 0 };
        var (service, _, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(Intent()));

        broker.StopPlaceCount.Should().Be(0, "建玉が生じないため保護対象が無い");
        result.StopPlaced.Should().BeNull();
        result.CoverageLost.Should().BeNull();
    }

    // ---- 見送り（逆指値を張れない Open は建玉を作らない・IADR-0210 決定1 / IADR-0211） ----

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task StopLossPriceの無いOpenは発注せず見送る(int? stopLoss)
    {
        var broker = new ScriptedBroker();
        var (service, store, _, reservations) = NewService(broker);
        var approved = Approved(Intent(stopLoss: stopLoss));

        var result = await service.ExecuteAsync(approved);

        var forgone = result.Forgone!;
        forgone.Reason.Should().Be(OrderDispatchForgoneReason.StopLossPriceMissing);
        result.Executed.Should().BeNull();
        store.GetAll().Should().BeEmpty("発注していない");
        reservations.Find(approved.DecisionId).Should().BeNull("発注に着手していないため予約も無い");
    }

    [Fact]
    public async Task 逆指値能力の無いブローカへのOpenは発注せず見送る()
    {
        // IProtectiveOrderBroker 非実装＝逆指値を張れない → 建玉を作らない側へ倒す（fail-closed）。
        var order = new BrokerOrder("o1", Intent(), OrderStatus.Filled, 10, 1_000m, Now, Now);
        var broker = new NonProtectiveBroker(order);
        var (service, store, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(Intent()));

        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.StopOrderUnsupported);
        broker.PlaceCount.Should().Be(0, "エントリー自体を発注しない");
        store.GetAll().Should().BeEmpty();
    }

    private sealed class NonProtectiveBroker(BrokerOrder order) : IBrokerAdapter
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;
        public int PlaceCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            PlaceCount++;
            return Task.FromResult(order);
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(order);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ---- 否定形: 決済（Close）には逆指値を張らない ----

    [Fact]
    public async Task Close注文には保護逆指値を張らない_否定形()
    {
        // 決済に逆指値を重ねると、決済後に残った逆指値が反対方向の建玉を生む（二重決済問題の裏面）。
        var broker = new ScriptedBroker { EntryStatus = OrderStatus.Filled, EntryFilled = 10 };
        var (service, _, _, _) = NewService(broker);
        var closeIntent = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 10, 1_000m, PositionEffect.Close);

        var result = await service.ExecuteAsync(new OrderApproved(Guid.NewGuid(), closeIntent, 10, Now));

        broker.StopPlaceCount.Should().Be(0);
        result.StopPlaced.Should().BeNull();
        result.Executed.Should().NotBeNull("Close は StopLossPrice なしで従来どおり執行される");
    }

    // ---- プロパティベース: 不変条件「建玉あり ⇒ 有効な逆指値あり（または人手対応の Critical）」----

    [Fact]
    public async Task 不変条件_建玉が残るなら有効な逆指値があるか人手対応が発火している()
    {
        // 疑似乱数（シード固定・再現可能）でエントリー約定・逆指値・取消・手仕舞いの挙動を振り、
        // どの組み合わせでも「逆指値なしの建玉が黙って残る」状態にならないことを検証する（issue #331 受け入れ基準）。
        var random = new Random(20260828);
        var entryStatuses = new[] { OrderStatus.Accepted, OrderStatus.PartiallyFilled, OrderStatus.Filled, OrderStatus.Rejected };

        for (var i = 0; i < 500; i++)
        {
            var entryStatus = entryStatuses[random.Next(entryStatuses.Length)];
            var filled = entryStatus switch
            {
                OrderStatus.Filled => 10,
                OrderStatus.PartiallyFilled => random.Next(1, 10),
                _ => 0,
            };
            var broker = new ScriptedBroker
            {
                EntryStatus = entryStatus,
                EntryFilled = filled,
                Stop = (StopBehavior)random.Next(4),
                Cancel = (RemedyBehavior)random.Next(2),
                MarketClose = (RemedyBehavior)random.Next(2),
                RequeryFilled = random.Next(3) switch { 0 => null, 1 => 0, _ => random.Next(1, 10) },
            };
            var (service, _, stops, _) = NewService(broker);
            var approved = Approved(Intent());

            var result = await service.ExecuteAsync(approved);

            // 建玉が残り得る = エントリーが終端失敗でなく、取消/手仕舞いで解消されていない。
            var positionMayRemain = entryStatus != OrderStatus.Rejected
                && result.CoverageLost?.Remediation is not (ProtectiveStopRemediation.EntryCancelled
                    or ProtectiveStopRemediation.PositionClosed);

            if (positionMayRemain)
            {
                var protectedByStop = result.StopPlaced is not null
                    && stops.Find(approved.DecisionId)?.State == ProtectiveStopState.Active;
                var humanAlerted = result.CoverageLost?.Remediation == ProtectiveStopRemediation.None;

                (protectedByStop || humanAlerted).Should().BeTrue(
                    $"逆指値なしの建玉が黙って残ってはならない（case {i}: entry={entryStatus}/{filled}"
                    + $" stop={broker.Stop} cancel={broker.Cancel} close={broker.MarketClose}）");
            }
        }
    }
}
