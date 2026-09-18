using AiStockTrading.Shared.Contracts.Events;

namespace OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;

// FR-05, FR-10, #331, IADR-0210/0211: 1 件の OrderApproved 処理の結果。
// 発注した（Executed）か見送った（Forgone）かは排他であり、Open のエントリー発注には
// 保護逆指値の結果（StopPlaced または CoverageLost）が付随し得る。
// 発行（Publish）は Worker 層（OrderApprovedHandler）が非 null のイベントに対して行う。
// FR-10, ADR-0040 決定1, #819, IADR-0342 決定6: S2 では StopPlaced / CoverageLost の代わりに StopWaived が付く
// （3 つは排他。末尾の任意項目として足し、既存の生成箇所を変えない）。
// FR-10, ADR-0040 決定1（S3）, #821, IADR-0347: S3 では StopAttempted（試行の記録＝注文種別と拒否理由）が
// **StopPlaced または CoverageLost と一緒に**付く（排他ではない）——結果の扱いは S0 と同じであり、
// 試行の記録はその手前の事実だからである。
public sealed record OrderDispatchResult(
    OrderExecuted? Executed,
    OrderDispatchForgone? Forgone,
    ProtectiveStopPlaced? StopPlaced,
    ProtectiveStopCoverageLost? CoverageLost,
    ProtectiveStopWaived? StopWaived = null,
    AlternativeProtectiveStopAttempted? StopAttempted = null)
{
    public static OrderDispatchResult FromExecuted(
        OrderExecuted executed,
        ProtectiveStopPlaced? stopPlaced = null,
        ProtectiveStopCoverageLost? coverageLost = null,
        ProtectiveStopWaived? stopWaived = null,
        AlternativeProtectiveStopAttempted? stopAttempted = null) =>
        new(executed, null, stopPlaced, coverageLost, stopWaived, stopAttempted);

    public static OrderDispatchResult FromForgone(OrderDispatchForgone forgone) =>
        new(null, forgone, null, null);
}
