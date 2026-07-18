using AiStockTrading.RiskManagement.Domain;

namespace AiStockTrading.RiskManagement.Application.State;

// FR-20, ADR-0008, IADR-0083: 撤退評価の結果。撤退判定（Assessment）に加え、この呼び出しで新規に kill switch を
// 起動したか（NewlyEngaged）を返す。定期評価ドライバ（#166）は「新規停止時のみ 1 回通知」の判定に本フラグを用いる。
// 判定を EvaluateWithdrawal 内（起動の可否と同一箇所）で確定させることで、ドライバ側の snapshot 比較（check-then-act）に
// 依存せず、手動評価エンドポイント（POST /stage-gate/withdrawal/evaluate）との同時起動での誤通知を避ける。
public sealed record WithdrawalEvaluationOutcome(WithdrawalAssessment Assessment, bool NewlyEngaged);
