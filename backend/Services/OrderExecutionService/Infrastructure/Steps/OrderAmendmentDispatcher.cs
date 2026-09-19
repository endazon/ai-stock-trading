using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.AmendOrder;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace OrderExecutionService.Infrastructure.Steps;

// FR-05, FR-19, FR-11, #154, #847, #768, IADR-0067, IADR-0357: 注文の訂正・取消を適用・永続化し、結果を発行する。
// Application 層（OrderAmendmentService）はメッセージ基盤を参照しない既存のレイヤリングを保つため、
// 発行は本 Worker 層が担う（OrderApprovedHandler が OrderExecuted を発行するのと同じ形）。
//
// 🔴 **駆動元は `PositionCloseCancellationHandler`（利用者による手仕舞いの取消）である**（#847 で配線した）。
// #768 が「DI 登録だけで本番の呼び出し元が無い」を台帳（UnwiredDiRegistrationTests）へ載せていた状態は解消した
// ——#141（リコンサイルの取消基点）・#152（pause による強制取消）は依然として本クラスを呼んでいない。
//
// 🔴 #847, IADR-0117（2026-09-19 追記・改定 1/4）: **`OrderCancelled` は「確実に取り消せた」ときだけ発行する。**
// このイベントは取引台帳で `MarkTerminal` → **在庫の押さえを解く引き金**であり、取消の結果が不明なまま出すと
// 同じ建玉に 2 本目の決済が並ぶ（二重決済で意図しないショート化）。確認は `OrderAmendmentService` が行う。
public sealed class OrderAmendmentDispatcher(
    OrderAmendmentService amendments,
    IMessageBus bus,
    ILogger<OrderAmendmentDispatcher> logger)
{
    /// <summary>
    /// DecisionId の注文を取り消す。<b>取り消せたと確認できたときだけ</b> <see cref="OrderCancelled"/> を発行する。
    /// </summary>
    public async Task<OrderCancellationOutcome> CancelAsync(
        Guid decisionId, string reason, CancellationToken cancellationToken = default)
    {
        var outcome = await amendments.CancelAsync(decisionId, reason, cancellationToken).ConfigureAwait(false);

        if (!outcome.Confirmed)
        {
            // 🔴 不明を確定として扱わない。押さえは残したまま、本当の終端は約定追跡（OrderFillPoller・
            // 30 秒周期）がブローカーから引き直して OrderExecuted として届ける（IADR-0117 改定 4 の経路）。
            logger.LogWarning(
                "注文取消を送信しましたが、取り消せたことを確認できませんでした"
                    + "（DecisionId={DecisionId} OrderId={OrderId} 照会した状態={Observed} 理由={Reason}）。"
                    + "取引台帳の在庫は押さえたまま残します（不明のまま解くと二重決済でショート化します）。"
                    + "本当の終端は約定追跡が引き直します。",
                outcome.DecisionId, outcome.OrderId, outcome.ObservedStatus, outcome.Reason);
            return outcome;
        }

        logger.LogInformation(
            "注文取消: DecisionId={DecisionId} OrderId={OrderId} 理由={Reason}",
            outcome.DecisionId, outcome.OrderId, outcome.Reason);

        // ADR-0013, IADR-0129, #354: Wolverine の PublishAsync は CancellationToken を取らない。
        await bus.PublishAsync(outcome.Event!).ConfigureAwait(false);
        return outcome;
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
