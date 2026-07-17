using AiStockTrading.RiskManagement.Domain;

namespace AiStockTrading.RiskManagement.Application.State;

// FR-20, UC-06, IADR-0070: 段階ゲートの現況（GET /risk-controls/stage-gate の応答）。
// 現段階・その設定（モード・資金上限）・遷移履歴（監査）・昇格評価・撤退評価をまとめて返す。
public sealed record StageGateStatus(
    TradingStage CurrentStage,
    StageSettings CurrentSettings,
    IReadOnlyList<StageTransition> History,
    PromotionAssessment Promotion,
    WithdrawalAssessment Withdrawal);
