using OrderExecutionService.Application.Ports;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;

namespace OrderExecutionService.Application.Services;

// FR-05, FR-19, FR-11, #154, IADR-0067: 発注済み注文の訂正・取消をブローカへ適用し、追記専用台帳へ永続化して
// 対応するイベント（OrderModified/OrderCancelled）を組み立てる。
//
// 本サービスは「訂正・取消が発生したら、適用され・永続化され・イベントになる」までの配管であり、
// **それを起こす駆動元（実ユースケース）は含まない**（IADR-0067 の境界）。時限取消・#141 の自動リコンサイル基点・
// #152 の pause 強制取消といったトリガは各 issue 側に残す。呼び出し口は Worker の OrderAmendmentDispatcher。
//
// 訂正・取消の口は IOrderAmendmentBroker（ペーパーのみが実装）で受ける。実ブローカー（moomoo）は本ポートを
// 実装しないため、実弾経路には訂正・取消の口が型として存在しない（fail-safe・IADR-0067）。
public sealed class OrderAmendmentService(
    IBrokerAdapter broker,
    IOrderAmendmentBroker amendmentBroker,
    IExecutedOrderStore executedOrders,
    IOrderLifecycleStore lifecycle,
    IClock clock)
{
    /// <summary>DecisionId の注文を取り消し、記録して <see cref="OrderCancelled"/> を返す。</summary>
    public async Task<OrderCancelled> CancelAsync(
        Guid decisionId, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);

        var orderId = ResolveOrderId(decisionId);

        // ブローカへ適用してから記録する。順序が逆だと、適用に失敗したのに取消済みの記録だけが残り、
        // 台帳とブローカ状態が食い違う（リコンサイル #141 の判断材料が壊れる）。
        await amendmentBroker.CancelOrderAsync(orderId, cancellationToken).ConfigureAwait(false);

        var now = clock.UtcNow;
        lifecycle.Append(new OrderLifecycleEvent(
            Guid.NewGuid(), decisionId, orderId, OrderLifecycleKind.Cancelled,
            PreviousQuantity: null, PreviousPrice: null, Quantity: null, Price: null, reason, now));

        return new OrderCancelled(decisionId, orderId, reason, now);
    }

    /// <summary>DecisionId の注文を訂正し、記録して <see cref="OrderModified"/> を返す。</summary>
    public async Task<OrderModified> ModifyAsync(
        Guid decisionId, int quantity, decimal price, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);

        var orderId = ResolveOrderId(decisionId);

        // 訂正前の値はブローカの現在値を権威とする。executed_orders は発注時の内容を保持したまま更新しないため、
        // そちらを訂正前として使うと2回目以降の訂正で誤った値を報告してしまう。
        var current = await broker.GetOrderAsync(orderId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"ブローカに注文が見つからない: OrderId={orderId}");

        var previousQuantity = current.Intent.Quantity;
        var previousPrice = current.Intent.Price;

        await amendmentBroker.ModifyOrderAsync(orderId, quantity, price, cancellationToken).ConfigureAwait(false);

        var now = clock.UtcNow;
        lifecycle.Append(new OrderLifecycleEvent(
            Guid.NewGuid(), decisionId, orderId, OrderLifecycleKind.Modified,
            previousQuantity, previousPrice, quantity, price, reason, now));

        return new OrderModified(
            decisionId, orderId, previousQuantity, previousPrice, quantity, price, reason, now);
    }

    // 注文 ID は発注結果（ExecutionRecord）が権威。発注前・未知の DecisionId は訂正・取消の対象にならない。
    private string ResolveOrderId(Guid decisionId) =>
        executedOrders.FindByDecisionId(decisionId)?.OrderId
        ?? throw new InvalidOperationException($"発注結果が見つからない: DecisionId={decisionId}");
}
