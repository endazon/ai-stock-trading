using System.Collections.Concurrent;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;

// FR-12: ペーパートレード用の仮想ブローカー。板寄せせず参照価格（OrderIntent.Price）で即時全量約定する。
// 判断・記録・報告のフローは実発注（moomoo アダプタ）と完全に同一とする。
//
// FR-05, FR-19, IADR-0067: 注文履歴テレメトリ（#154）の訂正・取消を成立させるため IOrderAmendmentBroker も実装する。
// 実ブローカー（moomoo）は本ポートを実装しない＝実弾経路には訂正・取消の口が型として存在しない（fail-safe）。
//
// FR-10, #331, IADR-0210: 保護注文（IProtectiveOrderBroker）も実装する——損切りはブローカー側逆指値へ
// 一本化されており、実装しないと paper 構成の Open 注文がすべて見送られる（逆指値なしの建玉を持たない・
// fail-closed）。paper の逆指値は板を持たないため**滞留 Accepted**（非終端）で受け、発火の模擬はしない。
// 成行手仕舞いは参照価格（CloseIntent.Price）で即時全量約定する。
public sealed class PaperBrokerAdapter : IBrokerAdapter, IOrderAmendmentBroker, IBrokerAvailabilityProbe, IProtectiveOrderBroker
{
    private readonly ConcurrentDictionary<string, BrokerOrder> _orders = new();
    private readonly TimeProvider _timeProvider;
    private readonly bool _immediateFill;

    // 状態遷移（訂正・取消）の read-modify-write を直列化する。immediateFill=false のときは注文が非終端で
    // 留まるため、同一注文への訂正・取消が競合し得る（既定の即時約定モードでは終端のみで到達しない）。
    private readonly Lock _gate = new();

    /// <param name="timeProvider">時刻源。既定はシステム時刻。</param>
    /// <param name="immediateFill">
    /// true（既定）: 発注は参照価格で即時全量約定する（FR-12 の既定挙動）。この場合すべての注文は即座に終端となるため、
    /// 訂正・取消は常に失敗する。
    /// false: 発注は非終端の <see cref="OrderStatus.Accepted"/> に留まり、訂正・取消が成立する（IADR-0067）。
    /// 注文履歴テレメトリ（#154）の配管を検証可能にするための最小の仕組みで、本番配線では既定（true）のまま用いる。
    /// </param>
    public PaperBrokerAdapter(TimeProvider? timeProvider = null, bool immediateFill = true)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _immediateFill = immediateFill;
    }

    /// <summary>
    /// FR-20, FR-12, #386, IADR-0149: 本アダプタの発注先は常に<b>内蔵 <c>paper</c></b>（外部へ一度も発注しない）。
    /// Stage 1 の合格判定には算入されない（<c>Stage1Aggregation.IsCounted</c> の許可制・IADR-0142 決定2）。
    /// </summary>
    public BrokerProvider Provider => BrokerProvider.InternalPaper;

    public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);

        var now = _timeProvider.GetUtcNow();

        // FR-05, FR-12, Issue #30: 実ブローカーが拒否する不正な注文（数量 0 以下・価格 0 以下）は
        // ペーパーでも約定させず、証券会社拒否（OrderStatus.Rejected）として記録する。判断・記録・報告の
        // フローを実発注と同一に保つため（Stage 0/1 の検証価値の維持）、ここで例外にせず終端の Rejected を返す。
        // この安全側判定は約定モードに依らず適用する。
        var isValid = IsValidOrder(intent.Quantity, intent.Price);

        var status = isValid
            ? (_immediateFill ? OrderStatus.Filled : OrderStatus.Accepted)
            : OrderStatus.Rejected;

        var order = new BrokerOrder(
            OrderId: Guid.NewGuid().ToString("N"),
            Intent: intent,
            Status: status,
            FilledQuantity: status == OrderStatus.Filled ? intent.Quantity : 0,
            AveragePrice: status == OrderStatus.Filled ? intent.Price : 0m,
            PlacedAt: now,
            // 終端に達していない受付状態は完了時刻を持たない（生存時間の終点が未確定・IADR-0040）。
            CompletedAt: status == OrderStatus.Accepted ? null : now);

        _orders[order.OrderId] = order;
        return Task.FromResult(order);
    }

    // FR-10, #331, IADR-0210: 保護逆指値。トリガー・注文内容が不正なら Rejected（実ブローカーの未受理と同じ分岐へ
    // 呼び出し側を導く）。受理は**滞留 Accepted**——paper は板を持たず発火を模擬しない（発火分岐はフェイクで検証する）。
    public Task<BrokerOrder> PlaceStopOrderAsync(
        OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(closeIntent);

        var now = _timeProvider.GetUtcNow();
        var valid = IsValidOrder(closeIntent.Quantity, triggerPrice);
        var order = new BrokerOrder(
            OrderId: Guid.NewGuid().ToString("N"),
            Intent: closeIntent,
            Status: valid ? OrderStatus.Accepted : OrderStatus.Rejected,
            FilledQuantity: 0,
            AveragePrice: 0m,
            PlacedAt: now,
            CompletedAt: valid ? null : now);

        _orders[order.OrderId] = order;
        return Task.FromResult(order);
    }

    // FR-10, #331, IADR-0210: 成行の手仕舞い（逆指値が成立しない場合の建玉解消）。参照価格で即時全量約定する。
    public Task<BrokerOrder> PlaceMarketOrderAsync(
        OrderIntent closeIntent, Guid decisionId, CancellationToken cancellationToken = default) =>
        PlaceOrderAsync(closeIntent, cancellationToken);

    public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken cancellationToken = default)
    {
        _orders.TryGetValue(orderId, out var order);
        return Task.FromResult(order);
    }

    /// <summary>
    /// FR-20, #385, IADR-0150: 内蔵 <c>paper</c> は同一プロセス内で完結するため<b>常に到達できる</b>。
    /// <para>
    /// 稼働として記録はされるが、発注先が内蔵 <c>paper</c> である日は Stage 1 の営業日に<b>算入されない</b>
    /// （許可制・IADR-0142 決定2）。SC-03 で「<c>paper</c> 稼働により N 日を除外」と別掲するために、
    /// 観測そのものは残す（IADR-0142 決定3）。
    /// </para>
    /// </summary>
    public Task<bool> IsOperationalAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }

    public Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var order = GetCancellableOrModifiable(orderId, "取消");

            _orders[orderId] = order with
            {
                Status = OrderStatus.Cancelled,
                CompletedAt = _timeProvider.GetUtcNow(),
            };
        }

        return Task.CompletedTask;
    }

    public Task<BrokerOrder> ModifyOrderAsync(
        string orderId, int quantity, decimal price, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var order = GetCancellableOrModifiable(orderId, "訂正");

            // 発注時（Issue #30）と同じ妥当性判定を訂正にも適用する。実ブローカーが拒否する内容へは訂正せず、
            // 元の注文も壊さない（例外を投げて呼び出し側に不成立を返す）。
            if (!IsValidOrder(quantity, price))
            {
                throw new InvalidOperationException(
                    $"不正な訂正内容（数量={quantity} 価格={price}）の注文は訂正できない: {orderId}");
            }

            var modified = order with
            {
                Intent = order.Intent with { Quantity = quantity, Price = price },
            };

            _orders[orderId] = modified;
            return Task.FromResult(modified);
        }
    }

    private static bool IsValidOrder(int quantity, decimal price) => quantity > 0 && price > 0m;

    // 訂正・取消の共通前提: 注文が存在し、かつ非終端であること。
    private BrokerOrder GetCancellableOrModifiable(string orderId, string operation)
    {
        if (!_orders.TryGetValue(orderId, out var order))
        {
            throw new InvalidOperationException($"注文が見つからない: {orderId}");
        }

        if (IsTerminal(order.Status))
        {
            throw new InvalidOperationException($"終了状態（{order.Status}）の注文は{operation}できない: {orderId}");
        }

        return order;
    }

    private static bool IsTerminal(OrderStatus status) =>
        status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Expired;
}
