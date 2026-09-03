using AiStockTrading.Shared.Contracts.Events;

namespace OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;

// FR-05, FR-10, #331, IADR-0210/0211: 1 件の OrderApproved 処理の結果。
// 発注した（Executed）か見送った（Forgone）かは排他であり、Open のエントリー発注には
// 保護逆指値の結果（StopPlaced または CoverageLost）が付随し得る。
// 発行（Publish）は Worker 層（OrderApprovedHandler）が非 null のイベントに対して行う。
public sealed record OrderDispatchResult(
    OrderExecuted? Executed,
    OrderDispatchForgone? Forgone,
    ProtectiveStopPlaced? StopPlaced,
    ProtectiveStopCoverageLost? CoverageLost)
{
    public static OrderDispatchResult FromExecuted(
        OrderExecuted executed,
        ProtectiveStopPlaced? stopPlaced = null,
        ProtectiveStopCoverageLost? coverageLost = null) =>
        new(executed, null, stopPlaced, coverageLost);

    public static OrderDispatchResult FromForgone(OrderDispatchForgone forgone) =>
        new(null, forgone, null, null);
}
