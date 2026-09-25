using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Tests;

// FR-10, #853, IADR-0428: 保護レグ（逆指値）の送信結果を分類ごとに注入できるブローカー（発注執行・ガード・突合の保護の試験で共用）。
// 🔴 数えるのは「送った回数」である。例外で終わっても（接続確立の失敗を除き）送信は済んでいる。
public sealed class StopLegScriptedBroker
    : IBrokerAdapter, IProtectiveOrderBroker, IAlternativeProtectiveOrderBroker, IBrokerPositionSource
{
    public enum StopBehavior
    {
        /// <summary>受理（Accepted）。</summary>
        Accept,

        /// <summary>確認できた拒否（終端 Rejected が返る）。</summary>
        Reject,

        /// <summary>接続確立の失敗＝確実に未発注。</summary>
        Unavailable,

        /// <summary>送信は済んだが結果を確認できない（返信待ちのタイムアウト）。</summary>
        Indeterminate,

        /// <summary>分類できない例外（未発注と言い切れない）。</summary>
        Unclassified,
    }

    /// <summary>ガードの試験で「失効した元の逆指値」として照会に Cancelled を返す注文 ID。</summary>
    public const string LapsedStopOrderId = "stop-1";

    public static readonly DateTimeOffset Now = new(2026, 9, 25, 7, 0, 0, TimeSpan.Zero);

    public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

    public OrderStatus EntryStatus { get; set; } = OrderStatus.Filled;
    public int EntryFilled { get; set; } = 10;
    public StopBehavior Stop { get; set; } = StopBehavior.Accept;

    /// <summary>成行手仕舞いが接続確立の失敗（確実に未発注）で終わる。false なら全量約定で返る。</summary>
    public bool MarketUnavailable { get; set; }

    public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } =
        [new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 10, 1_000m)];

    /// <summary>注文 ID → 照会結果（未登録で <see cref="LapsedStopOrderId"/> 以外は null＝照会不能）。</summary>
    public Dictionary<string, BrokerOrder?> Orders { get; } = new();

    public int EntryCount { get; private set; }
    public int StopPlaceCount { get; private set; }
    public int AlternativeStopCount { get; private set; }
    public int MarketCloseCount { get; private set; }
    public int CancelCount { get; private set; }
    public List<Guid> StopDecisionIds { get; } = [];
    public OrderIntent? LastStopIntent { get; private set; }

    public AlternativeProtectiveOrderType AlternativeProtectiveOrderType => AlternativeProtectiveOrderType.StopLimit;

    public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
    {
        EntryCount++;
        return Task.FromResult(new BrokerOrder(
            "entry-1", intent, EntryStatus, EntryFilled, EntryFilled > 0 ? intent.Price : 0m, Now,
            CompletedAt: OrderStatusLifecycle.IsTerminal(EntryStatus) ? Now : null));
    }

    public Task<BrokerOrder> PlaceStopOrderAsync(
        OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default)
    {
        StopPlaceCount++;
        return Task.FromResult(SendStop(closeIntent, decisionId));
    }

    public Task<AlternativeProtectiveOrderPlacement> PlaceAlternativeStopOrderAsync(
        OrderIntent closeIntent, decimal triggerPrice, decimal entryReferencePrice, Guid decisionId,
        CancellationToken cancellationToken = default)
    {
        AlternativeStopCount++;
        var order = SendStop(closeIntent, decisionId);
        return Task.FromResult(new AlternativeProtectiveOrderPlacement(
            order, AlternativeProtectiveOrderType.StopLimit,
            order.Status == OrderStatus.Rejected ? -1 : null,
            order.Status == OrderStatus.Rejected ? "拒否（テスト）" : null,
            order.Status == OrderStatus.Rejected ? null : order.OrderId));
    }

    private BrokerOrder SendStop(OrderIntent closeIntent, Guid decisionId)
    {
        StopDecisionIds.Add(decisionId);
        LastStopIntent = closeIntent;
        return Stop switch
        {
            StopBehavior.Accept => new BrokerOrder(
                $"stop-new-{StopDecisionIds.Count}", closeIntent, OrderStatus.Accepted, 0, 0m, Now, null),
            StopBehavior.Reject => new BrokerOrder(
                $"stop-new-{StopDecisionIds.Count}", closeIntent, OrderStatus.Rejected, 0, 0m, Now, Now),
            StopBehavior.Unavailable => throw new BrokerUnavailableException("OpenD 切断・逆指値は未発注（テスト）"),
            StopBehavior.Indeterminate => throw new BrokerDispatchIndeterminateException(
                "moomoo へ発注を送信しましたが結果を確認できませんでした（テスト）",
                new TimeoutException("返信待ちタイムアウト（テスト）")),
            _ => throw new InvalidOperationException("分類できない失敗（テスト）"),
        };
    }

    public Task<BrokerOrder> PlaceMarketOrderAsync(
        OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
    {
        MarketCloseCount++;
        return MarketUnavailable
            ? throw new BrokerUnavailableException("OpenD 切断・成行手仕舞いは未発注（テスト）")
            : Task.FromResult(new BrokerOrder(
                $"close-{MarketCloseCount}", closeIntent, OrderStatus.Filled,
                closeIntent.Quantity, closeIntent.Price, Now, Now));
    }

    public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default)
    {
        if (Orders.TryGetValue(orderId, out var order))
            return Task.FromResult(order);

        return Task.FromResult<BrokerOrder?>(orderId == LapsedStopOrderId
            ? new BrokerOrder(orderId, CloseIntent(10), OrderStatus.Cancelled, 0, 0m, Now, Now)
            : null);
    }

    public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
    {
        CancelCount++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
        Task.FromResult(Positions);

    public static OrderIntent CloseIntent(int quantity) =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
            quantity, 950m, PositionEffect.Close);
}
