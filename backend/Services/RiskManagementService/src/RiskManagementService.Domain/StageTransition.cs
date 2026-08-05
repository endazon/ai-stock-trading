namespace AiStockTrading.RiskManagement.Domain;

// FR-20, ADR-0008, UC-06: 段階遷移の型（承認フロー・履歴・撤退評価の結果）。

/// <summary>遷移の方向。昇格（Promotion）か差し戻し（Demotion）か。</summary>
public enum StageTransitionKind
{
    Promotion,
    Demotion,
}

// 段階遷移の合格/拒否理由。AssessPromotion の未充足基準および RequestTransition の拒否理由に用いる。
//
// **序数は HTTP 経路で整数として往来する**（Risk → Notification の `unmetCriteria` / `rejectionReasons`）。
// 既存メンバの序数を動かすと表示ラベルの対応が崩れるため、**値を明示し、追加は末尾へ行う**
// （RejectionReason と同じ規律。IADR-0134 決定2）。
public enum StageGateCriterion
{
    // 合格基準（§4）未充足
    BacktestNotPassed = 0,          // Stage 0→1: バックテスト未合格

    // 1 は旧 PaperDeviationUnexplained（「バックテストとの乖離が説明可能な範囲」）。
    // **計画 §4.1 が合格基準から削除した**（検証不能な条件を機械判定のゲートに据えると、判定できないか
    // 恣意的に判定されるかのどちらかになる）。検証の観点は月報の三者比較へ移設された。**再利用しない。**

    ControlViolationsPresent = 2,   // Stage 1→2: 統制違反あり（**クラス C 限定**。§4.1 条件 1）
    SlippageOrCostExceeded = 3,     // Stage 2→3: 実効スリッページ・費用が想定超過
    DailyLossLimitViolated = 4,     // Stage 2→3: 日次損失上限の運用違反あり

    // 遷移要求の構造的拒否
    NoUserApproval = 5,             // 承認者が空（承認なし）
    PromotionMustBeSequential = 6,  // 昇格は 1 段ずつ（飛び級不可）
    TargetIsCurrentStage = 7,       // 遷移先が現段階と同じ
    AlreadyAtTopStage = 8,          // 既に最上段（昇格先なし）

    // FR-20, #333, 06_daytrading-review §4.1〜§4.3, INDEX 決定 34・42: Stage 1（SIMULATE）の合格条件。
    // 期間と件数は**両方**を満たすまで昇格しない（片方だけでは昇格できない）。

    /// <summary>Stage 1→2: 実際に取引できた日数が目標（60 営業日）に届かない（§4.2）。</summary>
    Stage1TradingDaysInsufficient = 9,

    /// <summary>
    /// Stage 1→2: 取引件数が最小（100 件）に届かない（§4.1 条件 3）。
    /// **期間を満たしても件数は引き下げない**（§4.3）。
    /// </summary>
    Stage1TradeCountInsufficient = 10,

    /// <summary>
    /// Stage 1→2: 累計 120 営業日を経ても件数に届かず**打ち切り**（§4.3）。Stage 0 へ差し戻す。
    /// </summary>
    Stage1ExtensionExhausted = 11,

    /// <summary>
    /// FR-20, #387, §4.1 条件 1, IADR-0148: Stage 1→2: <b>統制違反件数の集計が供給されていない</b>。
    /// <para>
    /// <see cref="ControlViolationsPresent"/>（供給済みで 1 件以上）とは別の値である。
    /// 同じ値に潰すと、監査で「集計が来ていない」と「違反があった」を取り違える——
    /// 前者は供給経路の欠落（実装・運用の問題）、後者は AI が禁止事項へ抵触した記録であり、
    /// 打つ手がまったく違う。
    /// </para>
    /// </summary>
    ControlViolationCountUnavailable = 12,
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

/// <summary>
/// 撤退（差し戻し）判定の理由。**序数は HTTP 経路で整数として往来する**（値を明示し、追加は末尾へ）。
/// </summary>
public enum WithdrawalReason
{
    /// <summary>Stage 2/3: 実DD ≥ バックテスト最大DD × 倍率（ADR-0008）。</summary>
    DrawdownBreachedMultiple = 0,

    // 1 は旧 PaperDeviationUnexplained（Stage 1: バックテストとの乖離が説明不能）。
    // 計画 §4 は Stage 1 の差し戻しを「月報の三者比較を利用者が読んで判断する（**機械判定ではない**）」と
    // 定めたため、機械判定の撤退事由から外した。**再利用しない。**

    /// <summary>
    /// FR-20, #333, 06_daytrading-review §4.3, INDEX 決定 42:
    /// Stage 1: 累計 120 営業日を経ても取引件数が 100 件に届かず**打ち切り**。Stage 0 へ差し戻す。
    /// **これが Stage 1 における唯一の機械判定の撤退事由である。**
    /// </summary>
    Stage1ExtensionExhausted = 2,
}

// AssessWithdrawal の結果。撤退基準到達時に自動停止（HaltNewEntries）と降格提案（ProposedStage）を返す。
// 段階の実降格は提案に留め、確定は承認付き RequestTransition を要する（IADR-0041）。
public record WithdrawalAssessment(
    bool Triggered,
    WithdrawalReason? Reason,
    bool HaltNewEntries,
    TradingStage? ProposedStage);
