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

// FR-10, FR-11, FR-12, ADR-0040 決定1（S3）, #821, IADR-0347: 損切りの実行機構 S3（他のブローカー側注文種別）。
// 受け入れ基準:
//   1. SIMULATE ＋ S3 の発注で、**注文種別と拒否理由が監査に残る**（T-10-360）
//   2. 受理された場合は S0 と同じ保護レグとして扱う（T-10-361）
//   3. 拒否時の扱いは S0 と同じ＝建玉を持たない（T-10-362）
public class OrderExecutionServiceAlternativeStopTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 14, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // S0 の能力（IProtectiveOrderBroker）と S3 の能力（IAlternativeProtectiveOrderBroker）の**両方**を持つ
    // moomoo 相当のフェイク。S3 の既定は「拒否＋理由つき」（公式は模擬取引を指値・成行のみとしている）。
    private sealed class AlternativeCapableBroker(BrokerProvider provider)
        : IBrokerAdapter, IProtectiveOrderBroker, IAlternativeProtectiveOrderBroker
    {
        public BrokerProvider Provider { get; } = provider;

        public OrderStatus EntryStatus { get; init; } = OrderStatus.Filled;
        public bool AcceptAlternative { get; init; }
        public AlternativeProtectiveOrderType AlternativeProtectiveOrderType { get; init; }
            = AlternativeProtectiveOrderType.StopLimit;
        public Exception? AlternativeThrows { get; init; }

        public int PlaceCount { get; private set; }
        public int StopPlaceCount { get; private set; }
        public int AlternativePlaceCount { get; private set; }
        public int CancelCount { get; private set; }
        public int MarketCloseCount { get; private set; }
        public decimal LastTriggerPrice { get; private set; }
        public decimal LastEntryReferencePrice { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            PlaceCount++;
            var filled = EntryStatus == OrderStatus.Filled ? intent.Quantity : 0;
            return Task.FromResult(new BrokerOrder(
                "entry-1", intent, EntryStatus, filled, filled > 0 ? intent.Price : 0m, Now,
                EntryStatus == OrderStatus.Filled ? Now : null));
        }

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default)
        {
            StopPlaceCount++;
            return Task.FromResult(new BrokerOrder(
                "stop-1", closeIntent, OrderStatus.Rejected, 0, 0m, Now, Now));
        }

        public Task<AlternativeProtectiveOrderPlacement> PlaceAlternativeStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, decimal entryReferencePrice, Guid decisionId,
            CancellationToken ct = default)
        {
            AlternativePlaceCount++;
            LastTriggerPrice = triggerPrice;
            LastEntryReferencePrice = entryReferencePrice;
            if (AlternativeThrows is not null)
            {
                throw AlternativeThrows;
            }
            return Task.FromResult(AcceptAlternative
                ? new AlternativeProtectiveOrderPlacement(
                    new BrokerOrder("alt-1", closeIntent, OrderStatus.Accepted, 0, 0m, Now, null),
                    AlternativeProtectiveOrderType, null, null)
                : new AlternativeProtectiveOrderPlacement(
                    new BrokerOrder("alt-1", closeIntent, OrderStatus.Rejected, 0, 0m, Now, Now),
                    AlternativeProtectiveOrderType, 1, "Paper trading does not support StopLimit order"));
        }

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            MarketCloseCount++;
            return Task.FromResult(new BrokerOrder(
                "close-1", closeIntent, OrderStatus.Filled, closeIntent.Quantity, closeIntent.Price, Now, Now));
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            CancelCount++;
            return Task.CompletedTask;
        }
    }

    private static OrderIntent LongEntry() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 1_000m,
            PositionEffect.Open, StopLossPrice: 950m, FxRateToBase: 1m);

    private static (AppSvc Service, InMemoryExecutedOrderStore Store, InMemoryProtectiveStopOrderStore Stops)
        NewService(IBrokerAdapter broker)
    {
        var store = new InMemoryExecutedOrderStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        return (new AppSvc(broker, store, new InMemoryOrderReservationStore(), new FakeClock(), stops), store, stops);
    }

    private static OrderApproved Approved(OrderIntent intent) =>
        new(Guid.NewGuid(), intent, intent.Quantity, Now,
            StopLossMethod: StopLossExecutionMethod.AlternativeBrokerOrderType);

    // ---- 受け入れ基準 1（T-10-360）----

    [Fact]
    public async Task SIMULATEでS3は代替注文種別で発注し拒否理由が試行の記録に残る()
    {
        var broker = new AlternativeCapableBroker(BrokerProvider.MoomooSimulate) { EntryStatus = OrderStatus.Accepted };
        var (service, _, _) = NewService(broker);
        var approved = Approved(LongEntry());

        var result = await service.ExecuteAsync(approved);

        broker.AlternativePlaceCount.Should().Be(1, "S3 は代替注文種別で発注する");
        broker.StopPlaceCount.Should().Be(0, "S0 の OrderType_Stop は使わない");
        broker.LastTriggerPrice.Should().Be(950m, "発火価格は損切りライン");
        broker.LastEntryReferencePrice.Should().Be(1_000m, "トレール幅の基準はエントリーの判断価格");

        var attempted = result.StopAttempted!;
        attempted.EntryDecisionId.Should().Be(approved.DecisionId);
        attempted.StopDecisionId.Should().Be(ProtectiveStopIds.StopDecisionId(approved.DecisionId, 1));
        attempted.Symbol.Should().Be("AAPL");
        attempted.Market.Should().Be(Market.UnitedStates);
        attempted.OrderType.Should().Be(AlternativeProtectiveOrderType.StopLimit);
        attempted.Status.Should().Be(OrderStatus.Rejected);
        attempted.BrokerOrderId.Should().Be("alt-1");
        // 🔴 これが #821 の目的そのもの: 拒否理由（retType / retMsg）が監査へ運ばれること。
        attempted.RejectReasonCode.Should().Be(1);
        attempted.RejectReasonMessage.Should().Be("Paper trading does not support StopLimit order");
        attempted.Method.Should().Be(StopLossExecutionMethod.AlternativeBrokerOrderType);
        attempted.Provider.Should().Be(BrokerProvider.MoomooSimulate);
        attempted.OccurredAt.Should().Be(Now);
    }

    [Fact]
    public async Task S3の発注が例外で落ちても何の種別で試したかは記録に残る()
    {
        var broker = new AlternativeCapableBroker(BrokerProvider.MoomooSimulate)
        {
            EntryStatus = OrderStatus.Accepted,
            AlternativeProtectiveOrderType = AlternativeProtectiveOrderType.TrailingStop,
            AlternativeThrows = new BrokerUnavailableException("OpenD へ接続できません"),
        };
        var (service, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(LongEntry()));

        var attempted = result.StopAttempted!;
        attempted.OrderType.Should().Be(AlternativeProtectiveOrderType.TrailingStop);
        attempted.Status.Should().Be(OrderStatus.Rejected);
        attempted.BrokerOrderId.Should().BeNull();
        attempted.RejectReasonCode.Should().BeNull("ブローカーの応答自体が無い（retType は存在しない）");
        attempted.RejectReasonMessage.Should().Contain("OpenD");
        // 例外でも「逆指値なしの建玉を持たない」は変わらない。
        result.CoverageLost!.Remediation.Should().Be(ProtectiveStopRemediation.EntryCancelled);
    }

    // ---- 受け入れ基準 2（T-10-361）: 受理されたら S0 と同じ ----

    [Fact]
    public async Task S3が受理されたらS0と同じ保護レグとして記録されガードの巡回対象になる()
    {
        var broker = new AlternativeCapableBroker(BrokerProvider.MoomooSimulate)
        {
            EntryStatus = OrderStatus.Filled,
            AcceptAlternative = true,
        };
        var (service, store, stops) = NewService(broker);
        var approved = Approved(LongEntry());

        var result = await service.ExecuteAsync(approved);

        var stopDecisionId = ProtectiveStopIds.StopDecisionId(approved.DecisionId, 1);
        result.StopPlaced!.StopDecisionId.Should().Be(stopDecisionId);
        result.StopPlaced.TriggerPrice.Should().Be(950m);
        result.CoverageLost.Should().BeNull();
        result.StopWaived.Should().BeNull("S3 は免除ではない（保護レグを置いている）");

        // 受理されても「何で試したか」は残す（実測の一次証跡）。理由は無い。
        result.StopAttempted!.Status.Should().Be(OrderStatus.Accepted);
        result.StopAttempted.RejectReasonCode.Should().BeNull();
        result.StopAttempted.RejectReasonMessage.Should().BeNull();

        // S0 とまったく同じ記録: 逆指値レグの ExecutionRecord ＋ protective_stop_orders（ガードの巡回対象）。
        store.GetAll().Should().ContainSingle(r => r.DecisionId == stopDecisionId);
        stops.Find(approved.DecisionId)!.State.Should().Be(ProtectiveStopState.Active);
        broker.CancelCount.Should().Be(0);
        broker.MarketCloseCount.Should().Be(0);
    }

    // ---- 受け入れ基準 3（T-10-362）: 拒否時の扱いは S0 と同じ ----

    [Fact]
    public async Task S3の拒否は未約定ならエントリーを取り消す()
    {
        var broker = new AlternativeCapableBroker(BrokerProvider.MoomooSimulate) { EntryStatus = OrderStatus.Accepted };
        var (service, _, stops) = NewService(broker);
        var approved = Approved(LongEntry());

        var result = await service.ExecuteAsync(approved);

        result.CoverageLost!.Cause.Should().Be(ProtectiveStopLossCause.RejectedAtEntry);
        result.CoverageLost.Remediation.Should().Be(ProtectiveStopRemediation.EntryCancelled);
        broker.CancelCount.Should().Be(1);
        broker.MarketCloseCount.Should().Be(0);
        stops.Find(approved.DecisionId).Should().BeNull("未受理の保護レグは記録しない");
    }

    [Fact]
    public async Task S3の拒否は約定済みなら成行で手仕舞う()
    {
        var broker = new AlternativeCapableBroker(BrokerProvider.MoomooSimulate) { EntryStatus = OrderStatus.Filled };
        var (service, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(LongEntry()));

        result.CoverageLost!.Remediation.Should().Be(ProtectiveStopRemediation.PositionClosed);
        result.CoverageLost.Quantity.Should().Be(10);
        broker.MarketCloseCount.Should().Be(1);
        broker.CancelCount.Should().Be(0);
    }

    // 否定形: エントリーが終端失敗なら建玉が生じないため、S3 の試行自体を行わない。
    [Theory]
    [InlineData(OrderStatus.Rejected)]
    [InlineData(OrderStatus.Cancelled)]
    public async Task エントリーが生きていなければS3の試行は行われない(OrderStatus entryStatus)
    {
        var broker = new AlternativeCapableBroker(BrokerProvider.MoomooSimulate) { EntryStatus = entryStatus };
        var (service, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(LongEntry()));

        broker.AlternativePlaceCount.Should().Be(0);
        result.StopAttempted.Should().BeNull();
        result.CoverageLost.Should().BeNull();
    }
}
