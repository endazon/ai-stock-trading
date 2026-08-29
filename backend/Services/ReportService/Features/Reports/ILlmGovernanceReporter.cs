using AiStockTrading.Shared.Contracts.Llm;

namespace ReportService.Features.Reports;

// FR-06, FR-09, FR-11, ADR-0017 決定4-(2)/(3), #335, IADR-0217:
// 報告書生成で「割当どおりのモデルが応答しなかった」事実を外へ出すポート。
//
// ADR-0017 決定4 は可視化を 3 経路すべて実施すると定めた。本ポートが **②警告通知**（NotificationService が購読）と
// **③月報集計の供給元**（AuditService の台帳）を担う。①報告書のメタ情報は報告書本体が持つ。
//
// 🔴 **「恒常的に発火しているなら設定が誤っている」ため、埋もれない経路で出す**（同決定）。
// 報告書のモデルが恒常的に第 2 候補へ落ちていても、記録が無ければ誰も気づかない。
//
// 既定は NoOpLlmGovernanceReporter（publish しない＝fail-safe）。Worker が発行実装を配線する。
public interface ILlmGovernanceReporter
{
    Task FallbackFiredAsync(LlmAssignmentEvaluation evaluation, string purpose, CancellationToken cancellationToken = default);
}
