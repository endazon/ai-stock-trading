using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution.AmendOrder;

// FR-05, FR-19, FR-11, #154, #847, #768, IADR-0067, IADR-0357: 発注済み注文の訂正・取消をブローカへ適用し、
// 追記専用台帳へ永続化して対応するイベント（OrderModified/OrderCancelled）を組み立てる。
//
// 🔴 #847, IADR-0357: **取消は `IBrokerAdapter.CancelOrderAsync` で行う（moomoo も実装している）。**
// IADR-0067 が `IOrderAmendmentBroker` をペーパー専用にして型で塞いだのは「実 OpenD へ `TrdModifyOrder` を
// 配線していない」ことであり、**取消は当初から `IBrokerAdapter` に在って moomoo が実装している**
// （`MoomooBrokerAdapter.CancelOrderAsync` → `client.CancelOrderAsync`。`ProtectiveStopGuard` も
// `OrderExecutionAppService` も既にそれを呼んでいる）。訂正（`ModifyAsync`）だけが従来どおり
// `IOrderAmendmentBroker` を要し、無い構成では `NotSupportedException` になる。
//
// 🔴 #847, IADR-0117（2026-09-19 追記・改定 1/4）: **「確実に取り消せた」と「取消を送ったが結果が不明」を
// 混同しない。** `OrderCancelled` は取引台帳で `MarkTerminal` → **在庫の押さえを解く引き金**である。
// ブローカーが取消**要求**を受理しても注文はまだ生きていることがある（moomoo は `Cancelling_Part`/`Cancelling_All`
// を経て `Cancelled_All` になり、その間に約定し得る）。不明のまま押さえを解くと、同じ建玉に 2 本目の決済が
// 並ぶ（**二重決済で意図しないショート化**）。したがって取消の送信後に**注文状態を照会して確認**し、
// 確認できた終端（`AbandonsUnfilledRemainder`）のときだけイベントを組み立てる。
public sealed class OrderAmendmentService(
    IBrokerAdapter broker,
    IExecutedOrderStore executedOrders,
    IOrderLifecycleStore lifecycle,
    IClock clock,
    IOrderAmendmentBroker? amendmentBroker = null)
{
    /// <summary>
    /// DecisionId の注文を取り消し、記録して結果を返す。
    /// <b>「確実に取り消せた」と確認できたときだけ</b> <see cref="OrderCancellationOutcome.Event"/> が入る。
    /// </summary>
    public async Task<OrderCancellationOutcome> CancelAsync(
        Guid decisionId, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);

        var orderId = ResolveOrderId(decisionId);

        // ブローカへ適用してから記録する。順序が逆だと、適用に失敗したのに取消済みの記録だけが残り、
        // 台帳とブローカ状態が食い違う（リコンサイル #141 の判断材料が壊れる）。
        // 失敗（例外）はそのまま伝播させる —— 記録もイベントも作らない。
        await broker.CancelOrderAsync(orderId, cancellationToken).ConfigureAwait(false);

        var now = clock.UtcNow;

        // 取消を**送った**ことは無条件に監査へ残す（誰が・なぜは駆動元のイベントが持つ）。
        // 記録することと在庫を戻すことは別である —— 後者だけが確認を要する。
        lifecycle.Append(new OrderLifecycleEvent(
            Guid.NewGuid(), decisionId, orderId, OrderLifecycleKind.Cancelled,
            PreviousQuantity: null, PreviousPrice: null, Quantity: null, Price: null, reason, now));

        // 🔴 確認: ブローカーが**未約定残の放棄が確定した終端**を返したときだけ確定とみなす。
        // 照会できない（null＝不明）・まだ非終端（取消進行中を含む）・全量約定は、いずれも**確定ではない**。
        // 確定しない場合、本当の終端は既存の約定追跡（OrderFillPoller・30 秒周期）が観測して
        // OrderExecuted として届ける（IADR-0117 改定 4 の経路。新しい経路を作らない）。
        var snapshot = await broker.GetOrderAsync(orderId, cancellationToken).ConfigureAwait(false);
        var confirmed = snapshot is { } observed && OrderStatusLifecycle.AbandonsUnfilledRemainder(observed.Status);

        return new OrderCancellationOutcome(
            decisionId,
            orderId,
            reason,
            snapshot?.Status,
            // #847, IADR-0357: 確認に使った照会は**累積約定数も返している**。それを載せる ——
            // 取消は部分約定を追い越して台帳へ着くため、載せないと失効通知の「未決済 N 株」が水増しされる。
            confirmed
                ? new OrderCancelled(decisionId, orderId, reason, now, snapshot!.FilledQuantity)
                : null);
    }

    /// <summary>DecisionId の注文を訂正し、記録して <see cref="OrderModified"/> を返す。</summary>
    public async Task<OrderModified> ModifyAsync(
        Guid decisionId, int quantity, decimal price, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);

        // #847, IADR-0067: 実ブローカー（moomoo）へ TrdModifyOrder を配線していないため、訂正の口は
        // ペーパーだけが持つ。無い構成で黙って取消＋再発注へ読み替えたりしない（別の注文になる）。
        var amendments = amendmentBroker
            ?? throw new NotSupportedException(
                "この発注先には注文訂正の口がありません（訂正は内蔵 paper のみ）。取消は利用できます。");

        var orderId = ResolveOrderId(decisionId);

        // 訂正前の値はブローカの現在値を権威とする。executed_orders は発注時の内容を保持したまま更新しないため、
        // そちらを訂正前として使うと2回目以降の訂正で誤った値を報告してしまう。
        var current = await broker.GetOrderAsync(orderId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"ブローカに注文が見つからない: OrderId={orderId}");

        var previousQuantity = current.Intent.Quantity;
        var previousPrice = current.Intent.Price;

        await amendments.ModifyOrderAsync(orderId, quantity, price, cancellationToken).ConfigureAwait(false);

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

/// <summary>
/// FR-05, FR-10, FR-11, UC-06, #847, IADR-0357: 取消 1 回の結果。
/// <para>
/// 🔴 <see cref="Event"/> が <c>null</c>＝<b>取消を送ったが、取り消せたと確認できていない</b>。
/// このとき取引台帳は在庫を押さえたままにする（押さえを解くと二重決済でショート化する）。
/// <see cref="ObservedStatus"/> は確認のために照会した状態（<c>null</c>＝照会できなかった＝不明）で、
/// <b>診断用</b>である（判定に使うのは <see cref="Confirmed"/> だけ）。
/// </para>
/// </summary>
public sealed record OrderCancellationOutcome(
    Guid DecisionId,
    string OrderId,
    string Reason,
    OrderStatus? ObservedStatus,
    OrderCancelled? Event)
{
    /// <summary>取り消せたと<b>確認できた</b>（＝在庫の押さえを解いてよい）。</summary>
    public bool Confirmed => Event is not null;
}
