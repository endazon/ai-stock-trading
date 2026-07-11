namespace AiStockTrading.RiskManagement.Domain;

// FR-20, ADR-0008, UC-06: 段階遷移の型（承認フロー・履歴・撤退評価の結果）。

/// <summary>遷移の方向。昇格（Promotion）か差し戻し（Demotion）か。</summary>
public enum StageTransitionKind
{
    Promotion,
    Demotion,
}

// 段階遷移の合格/拒否理由。AssessPromotion の未充足基準および RequestTransition の拒否理由に用いる。
public enum StageGateCriterion
{
    // 合格基準（§4）未充足
    BacktestNotPassed,          // Stage 0→1: バックテスト未合格
    PaperDeviationUnexplained,  // Stage 1→2: バックテストとの乖離が説明不能
    ControlViolationsPresent,   // Stage 1→2: 統制違反あり
    SlippageOrCostExceeded,     // Stage 2→3: 実効スリッページ・費用が想定超過
    DailyLossLimitViolated,     // Stage 2→3: 日次損失上限の運用違反あり

    // 遷移要求の構造的拒否
    NoUserApproval,             // 承認者が空（承認なし）
    PromotionMustBeSequential,  // 昇格は 1 段ずつ（飛び級不可）
    TargetIsCurrentStage,       // 遷移先が現段階と同じ
    AlreadyAtTopStage,          // 既に最上段（昇格先なし）
}

// FR-20, UC-06: 利用者の承認（Discord/チャット UI 由来）。承認者が空なら承認なしとして扱う。
public record StageApproval(TradingStage TargetStage, string ApprovedBy);

// FR-20: 遷移履歴の 1 件（不変・監査対象）。承認者・時刻・from/to・理由を保持する。
public record StageTransition(
    int Sequence,
    TradingStage FromStage,
    TradingStage ToStage,
    StageTransitionKind Kind,
    string ApprovedBy,
    DateTimeOffset OccurredAtUtc,
    string Reason);

// AssessPromotion の結果。昇格先（最上段なら null）と合格可否・未充足基準。
public record PromotionAssessment(
    TradingStage? TargetStage,
    bool Eligible,
    IReadOnlyList<StageGateCriterion> UnmetCriteria);

// RequestTransition の結果。受理時は Transition と ResultingSettings、拒否時は RejectionReasons を返す。
public record StageTransitionResult(
    bool Accepted,
    StageTransition? Transition,
    StageSettings? ResultingSettings,
    IReadOnlyList<StageGateCriterion> RejectionReasons);

/// <summary>撤退（差し戻し）判定の理由。</summary>
public enum WithdrawalReason
{
    DrawdownBreachedMultiple,   // Stage 2/3: 実DD ≥ バックテスト最大DD × 倍率
    PaperDeviationUnexplained,  // Stage 1: バックテストとの乖離が説明不能
}

// AssessWithdrawal の結果。撤退基準到達時に自動停止（HaltNewEntries）と降格提案（ProposedStage）を返す。
// 段階の実降格は提案に留め、確定は承認付き RequestTransition を要する（IADR-0037）。
public record WithdrawalAssessment(
    bool Triggered,
    WithdrawalReason? Reason,
    bool HaltNewEntries,
    TradingStage? ProposedStage);
