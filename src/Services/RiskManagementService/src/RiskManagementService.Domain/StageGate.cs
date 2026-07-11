namespace AiStockTrading.RiskManagement.Domain;

// FR-20, ADR-0008, UC-06, IADR-0037: 段階ゲートの遷移管理（状態機械＋承認フロー・純ロジック）。
// 段階遷移（StageTransition）を生成する唯一の経路が承認付き RequestTransition であり、承認欠如時の遷移を
// 構造的に不可能にする。撤退は AssessWithdrawal で「自動停止＋降格提案」に分離する（段階変更は承認を要する）。
public static class StageGate
{
    // FR-20, 06_daytrading-review §4: 次段階への昇格が合格基準を満たすか評価する。
    // 昇格先＝現段階の 1 段上（最上段なら null）。段階別に §4 の合格基準を評価する。
    public static PromotionAssessment AssessPromotion(
        TradingStage current, StagePerformance performance)
    {
        var target = NextStage(current);
        if (target is null)
        {
            return new PromotionAssessment(
                TargetStage: null, Eligible: false, UnmetCriteria: [StageGateCriterion.AlreadyAtTopStage]);
        }

        var unmet = UnmetPromotionCriteria(current, performance);
        return new PromotionAssessment(target, Eligible: unmet.Count == 0, UnmetCriteria: unmet);
    }

    // FR-20, UC-06: 承認による段階遷移。承認者が空なら昇格・差し戻しとも拒否する（承認なしに遷移しない）。
    // 昇格は 1 段ずつ・合格基準充足を要する。差し戻し（段階を下げる方向）は安全側のため承認のみで受理する。
    public static StageTransitionResult RequestTransition(
        TradingStage current,
        int nextSequence,
        StageApproval approval,
        StagePerformance performance,
        StageGatePolicy policy,
        DateTimeOffset now)
    {
        // 承認なしに段階が遷移しない（受け入れ基準）。承認者が空なら拒否する。
        if (string.IsNullOrWhiteSpace(approval.ApprovedBy))
        {
            return Reject(StageGateCriterion.NoUserApproval);
        }

        var target = approval.TargetStage;
        if (target == current)
        {
            return Reject(StageGateCriterion.TargetIsCurrentStage);
        }

        StageTransitionKind kind;
        string reason;
        if (target > current)
        {
            // 昇格: 1 段ずつ（飛び級不可）かつ合格基準充足が必要
            if ((int)target != (int)current + 1)
            {
                return Reject(StageGateCriterion.PromotionMustBeSequential);
            }

            var unmet = UnmetPromotionCriteria(current, performance);
            if (unmet.Count > 0)
            {
                return new StageTransitionResult(
                    Accepted: false, Transition: null, ResultingSettings: null, RejectionReasons: unmet);
            }

            kind = StageTransitionKind.Promotion;
            reason = "利用者承認による昇格";
        }
        else
        {
            // 差し戻し: 段階を下げる方向は安全側。承認があれば合格基準不問で受理する（ADR-0008）
            kind = StageTransitionKind.Demotion;
            reason = "利用者承認による差し戻し";
        }

        var transition = new StageTransition(
            nextSequence, current, target, kind, approval.ApprovedBy, now, reason);
        return new StageTransitionResult(
            Accepted: true,
            Transition: transition,
            ResultingSettings: policy.SettingsFor(target),
            RejectionReasons: []);
    }

    // FR-20, ADR-0008: 撤退（差し戻し）基準の評価。到達時は自動停止（HaltNewEntries）と降格提案（ProposedStage）を返す。
    // Stage 2/3: 実DD ≥ バックテスト最大DD × 倍率 で自動停止＋Stage 0 再検証提案。Stage 1: 乖離が説明不能で Stage 0 差し戻し提案。
    // 段階の実降格は提案に留め、確定は承認付き RequestTransition を要する（自動＝停止、承認＝段階変更）。
    public static WithdrawalAssessment AssessWithdrawal(
        TradingStage current, StagePerformance performance, StageGatePolicy policy)
    {
        switch (current)
        {
            case TradingStage.Stage1Paper when !performance.PaperDeviationExplained:
                // ペーパー段階の乖離が説明不能 → Stage 0 へ差し戻し提案（ペーパーのため即時停止は不要）
                return new WithdrawalAssessment(
                    Triggered: true,
                    Reason: WithdrawalReason.PaperDeviationUnexplained,
                    HaltNewEntries: false,
                    ProposedStage: TradingStage.Stage0Verification);

            case TradingStage.Stage2MinimalLive or TradingStage.Stage3ScaledLive
                when performance.BacktestMaxDrawdownRatio > 0m
                    && performance.ObservedMaxDrawdownRatio
                        >= performance.BacktestMaxDrawdownRatio * policy.WithdrawalDrawdownMultiple:
                // 実弾段階で実DD がバックテスト最大DD の倍率超 → 自動停止＋Stage 0 再検証提案（ADR-0008）
                return new WithdrawalAssessment(
                    Triggered: true,
                    Reason: WithdrawalReason.DrawdownBreachedMultiple,
                    HaltNewEntries: true,
                    ProposedStage: TradingStage.Stage0Verification);

            default:
                return new WithdrawalAssessment(
                    Triggered: false, Reason: null, HaltNewEntries: false, ProposedStage: null);
        }
    }

    // 段階別の未充足合格基準（§4）を列挙する。昇格先が無い最上段では呼ばない。
    private static List<StageGateCriterion> UnmetPromotionCriteria(
        TradingStage current, StagePerformance performance)
    {
        var unmet = new List<StageGateCriterion>();
        switch (current)
        {
            case TradingStage.Stage0Verification:
                // FR-15: DSR 補正後もエッジが正・最大DDが許容内（バックテスト合格）
                if (!performance.BacktestPassed)
                {
                    unmet.Add(StageGateCriterion.BacktestNotPassed);
                }

                break;

            case TradingStage.Stage1Paper:
                // バックテストとの乖離が説明可能・統制違反 0 件
                if (!performance.PaperDeviationExplained)
                {
                    unmet.Add(StageGateCriterion.PaperDeviationUnexplained);
                }

                if (performance.ControlViolationCount > 0)
                {
                    unmet.Add(StageGateCriterion.ControlViolationsPresent);
                }

                break;

            case TradingStage.Stage2MinimalLive:
                // 実効スリッページ・費用が想定内・日次損失上限の運用実績
                if (!performance.SlippageAndCostWithinExpected)
                {
                    unmet.Add(StageGateCriterion.SlippageOrCostExceeded);
                }

                if (!performance.DailyLossLimitRespected)
                {
                    unmet.Add(StageGateCriterion.DailyLossLimitViolated);
                }

                break;
        }

        return unmet;
    }

    private static TradingStage? NextStage(TradingStage stage) =>
        stage == TradingStage.Stage3ScaledLive ? null : (TradingStage)((int)stage + 1);

    private static StageTransitionResult Reject(params StageGateCriterion[] reasons) =>
        new(Accepted: false, Transition: null, ResultingSettings: null, RejectionReasons: reasons);
}
