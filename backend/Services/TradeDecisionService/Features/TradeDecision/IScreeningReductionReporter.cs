using AiStockTrading.Shared.Contracts.Events;

namespace TradeDecisionService.Features.TradeDecision;

// FR-02, FR-04, FR-06, FR-11, #337, IADR-0247: スクリーニング入力の縮退発生を記録経路（監査台帳・月報集計）へ
// 渡すポート。既定は NoOpScreeningReductionReporter（publish しない）。Worker が Publishing 実装を配線する。
// 計画（planning#53 の裁定）は「切り詰めが発生した事実は記録し、月報に件数を記載する」と定める——
// **記録が無いと「静かに判断材料が減っていた」状態になる**ため、縮退が有効な構成では必ず配線する。
public interface IScreeningReductionReporter
{
    Task ReportAsync(ScreeningContextReduced reduction, CancellationToken cancellationToken = default);
}
