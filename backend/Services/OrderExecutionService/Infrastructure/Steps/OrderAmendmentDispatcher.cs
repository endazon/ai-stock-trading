using OrderExecutionService.Features.OrderExecution;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace OrderExecutionService.Infrastructure.Steps;

// FR-05, FR-19, FR-11, #154, IADR-0067: 注文の訂正・取消を適用・永続化し、結果を発行する。
// Application 層（OrderAmendmentService）はメッセージ基盤を参照しない既存のレイヤリングを保つため、
// 発行は本 Worker 層が担う（OrderApprovedHandler が OrderExecuted を発行するのと同じ形）。
//
// **本 PR の時点で呼び出し元は存在しない**。訂正・取消を起こす駆動元（実ユースケース）は IADR-0067 の境界により
// 対象外で、以下の issue が本クラスを呼ぶ:
//   - #141: 発注予約の自動リコンサイル（Reserved 滞留をブローカ照会で解消）の取消基点
//   - #152: 取引の一時停止（pause）による未約定注文の強制取消
// 時限取消も同様に駆動元側で実装する。ここは「発生したら発行・永続化・供給される」までの配管である。
public sealed class OrderAmendmentDispatcher(
    OrderAmendmentService amendments,
    IMessageBus bus,
    ILogger<OrderAmendmentDispatcher> logger)
{
    /// <summary>DecisionId の注文を取り消し、<see cref="OrderCancelled"/> を発行する。</summary>
    public async Task<OrderCancelled> CancelAsync(
        Guid decisionId, string reason, CancellationToken cancellationToken = default)
    {
        var cancelled = await amendments.CancelAsync(decisionId, reason, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "注文取消: DecisionId={DecisionId} OrderId={OrderId} 理由={Reason}",
            cancelled.DecisionId, cancelled.OrderId, cancelled.Reason);

        // ADR-0013, IADR-0129, #354: Wolverine の PublishAsync は CancellationToken を取らない。
        await bus.PublishAsync(cancelled).ConfigureAwait(false);
        return cancelled;
    }

    /// <summary>DecisionId の注文を訂正し、<see cref="OrderModified"/> を発行する。</summary>
    public async Task<OrderModified> ModifyAsync(
        Guid decisionId, int quantity, decimal price, string reason, CancellationToken cancellationToken = default)
    {
        var modified = await amendments
            .ModifyAsync(decisionId, quantity, price, reason, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "注文訂正: DecisionId={DecisionId} OrderId={OrderId} 数量={Previous}→{Quantity} 価格={PrevPrice}→{Price} 理由={Reason}",
            modified.DecisionId, modified.OrderId, modified.PreviousQuantity, modified.Quantity,
            modified.PreviousPrice, modified.Price, modified.Reason);

        await bus.PublishAsync(modified).ConfigureAwait(false);
        return modified;
    }
}
