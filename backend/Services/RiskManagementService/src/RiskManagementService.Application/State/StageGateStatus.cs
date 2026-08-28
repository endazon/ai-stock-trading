using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Kernel.Trading;

namespace AiStockTrading.RiskManagement.Application.State;

// FR-20, UC-06, IADR-0070: 段階ゲートの現況（GET /risk-controls/stage-gate の応答）。
// 現段階・その設定（モード・資金上限）・遷移履歴（監査）・昇格評価・撤退評価をまとめて返す。
public sealed record StageGateStatus(
    TradingStage CurrentStage,
    StageSettings CurrentSettings,
    IReadOnlyList<StageTransition> History,
    PromotionAssessment Promotion,
    WithdrawalAssessment Withdrawal,
    // FR-20, SC-03, #334, IADR-0142: Stage 1 の進捗（**moomoo SIMULATE の実績のみ**）と、
    // 内蔵 paper 稼働により算入されなかった営業日数。画面は「経過 42 / 60 営業日
    // （paper 稼働により 3 日を除外）」と併記する——**算入されなかった期間があること自体**が
    // 見えないと、進捗の数字を説明できなくなる（05_screens SC-03）。
    Stage1Progress Stage1Progress,
    // 目標営業日数 60 / 最小取引件数 100 / 打ち切り 120（06_daytrading-review §4.1〜§4.3）。
    // 画面が閾値を直書きすると計画の改訂に追随しないため、現況と同じ応答で返す。
    Stage1GateCriteria Stage1Criteria);
