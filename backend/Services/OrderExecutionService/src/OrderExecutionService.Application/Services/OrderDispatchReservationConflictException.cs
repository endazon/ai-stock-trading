namespace AiStockTrading.OrderExecution.Application.Services;

// #131, FR-05, IADR-0057: 予約済みだが未確定の DecisionId を再処理しようとしたときに投げる。
// 「未発注」と「発注済みだが記録できていない」の区別が付かないため、安全側（at-most-once）に倒して
// 再発注を拒否する。MassTransit の再試行を使い切ると _error キューへ送られ、リコンサイル対象になる。
public sealed class OrderDispatchReservationConflictException(Guid decisionId)
    : InvalidOperationException(
        $"DecisionId={decisionId} は発注予約済みで未確定です。ブローカへ発注済みか否かを判定できないため、" +
        "二重発注を避けて再発注を拒否します。ブローカ側の注文状態を確認してリコンサイルしてください。")
{
    public Guid DecisionId { get; } = decisionId;
}
