using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace AiStockTrading.RiskManagement.Infrastructure.Composable.Steps;

// FR-10, UC-01, UC-02, ADR-0003: 取引判断（TradeDecisionMade）を購読し、発注前に決定的に検証して
// OrderApproved / OrderRejected を発行する。判定ロジックは Application 層の OrderScreeningService に委譲する。
//
// ADR-0013, IADR-0129, #354: MassTransit の IConsumer<TradeDecisionMade> から Wolverine のハンドラへ移行した。
// **本サービスは OrderApproved を発行しつつ自分でも購読している**（台帳・活動射影）。Wolverine の既定では
// 発行が自プロセス内に閉じ、OrderExecutionService へ一通も届かない（発注が一件も執行されない）。
// これを止めるのが IADR-0129 決定 3（DisableConventionalLocalRouting・共通ヘルパが強制する）である。
public sealed class TradeDecisionMadeHandler(
    OrderScreeningService screeningService,
    IControlViolationObservationStore violationStore,
    ILogger<TradeDecisionMadeHandler> logger)
{
    public async Task Handle(TradeDecisionMade message, IMessageBus bus)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(bus);

        var outcome = screeningService.Screen(message);

        // FR-20, FR-11, #387, IADR-0148: 審査結果を段階ゲートの観測ログへ記録する（承認・拒否のいずれも）。
        // **イベント発行より先に記録する**——発行が失敗して再送されても DecisionId で冪等であり、
        // 逆順にすると「発行できたが観測が落ちた」拒否が生まれ、違反件数が過小になる（緩い側）。
        violationStore.Record(outcome.Observation);

        if (outcome.IsApproved)
        {
            logger.LogInformation(
                "注文承認: DecisionId={DecisionId} 数量={Quantity}",
                message.DecisionId, outcome.Approved!.ApprovedQuantity);
            await bus.PublishAsync(outcome.Approved!).ConfigureAwait(false);
        }
        else
        {
            // FR-11: 拒否理由は監査・通知のため理由列挙つきで発行する。
            logger.LogWarning(
                "注文拒否: DecisionId={DecisionId} 理由={Reasons}",
                message.DecisionId, string.Join(",", outcome.Rejected!.Reasons));
            await bus.PublishAsync(outcome.Rejected!).ConfigureAwait(false);
        }
    }
}
