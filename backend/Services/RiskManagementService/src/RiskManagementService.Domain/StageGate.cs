using AiStockTrading.Shared.Kernel.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-20, ADR-0008, UC-06, IADR-0041: 段階ゲートの遷移管理（状態機械＋承認フロー・純ロジック）。
// 段階遷移（StageTransition）を生成する唯一の経路が承認付き RequestTransition であり、承認欠如時の遷移を
// 構造的に不可能にする。撤退は AssessWithdrawal で「自動停止＋降格提案」に分離する（段階変更は承認を要する）。
public static class StageGate
{
    // FR-20, 06_daytrading-review §4: 次段階への昇格が合格基準を満たすか評価する。
    // 昇格先＝現段階の 1 段上（最上段なら null）。段階別に §4 の合格基準を評価する。
    //
    // FR-20, #387, IADR-0148: 統制違反件数（条件 1）は StagePerformance に持たせず**必須引数**で受ける。
    // null＝未供給であり、**条件未充足として扱う**（0 件と同一視しない）。
    public static PromotionAssessment AssessPromotion(
        TradingStage current,
        StagePerformance performance,
        ControlViolationTally? controlViolations,
        StageGatePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var target = NextStage(current);
        if (target is null)
        {
            return new PromotionAssessment(
                TargetStage: null, Eligible: false, UnmetCriteria: [StageGateCriterion.AlreadyAtTopStage]);
        }

        var unmet = UnmetPromotionCriteria(current, performance, controlViolations, policy);
        return new PromotionAssessment(target, Eligible: unmet.Count == 0, UnmetCriteria: unmet);
    }

    // FR-20, UC-06: 承認による段階遷移。承認者が空なら昇格・差し戻しとも拒否する（承認なしに遷移しない）。
    // 昇格は 1 段ずつ・合格基準充足を要する。差し戻し（段階を下げる方向）は安全側のため承認のみで受理する。
    public static StageTransitionResult RequestTransition(
        TradingStage current,
        int nextSequence,
        StageApproval approval,
        StagePerformance performance,
        ControlViolationTally? controlViolations,
        StageGatePolicy policy,
        DateTimeOffset now)
    {
        // #466, IADR-0180 決定1: 以降のすべての return が `policy.Stage1Criteria` を載せるため、
        // policy は**全経路で必須**になった（従来は拒否経路が policy に触れずに戻れた）。
        // AssessPromotion と同じ形で入口に置き、null は NullReferenceException ではなく引数名つきで落とす。
        ArgumentNullException.ThrowIfNull(policy);

        // FR-20, FR-11, SC-02, #466, §4.1 追補3, IADR-0180: 応答が運ぶ**実効の**合格条件。
        // `StageGateService.EffectivePolicy()` が設定値（Stage1MinimumTradeCount）を重ねた後の値であり、
        // ここで新たに供給元を作らない（供給元が 2 つになれば必ず食い違う）。
        var criteria = policy.Stage1Criteria;

        // 承認なしに段階が遷移しない（受け入れ基準）。承認者が空なら拒否する。
        if (string.IsNullOrWhiteSpace(approval.ApprovedBy))
        {
            return Reject(criteria, StageGateCriterion.NoUserApproval);
        }

        var target = approval.TargetStage;
        if (target == current)
        {
            return Reject(criteria, StageGateCriterion.TargetIsCurrentStage);
        }

        StageTransitionKind kind;
        string reason;
        if (target > current)
        {
            // 昇格: 1 段ずつ（飛び級不可）かつ合格基準充足が必要
            if ((int)target != (int)current + 1)
            {
                return Reject(criteria, StageGateCriterion.PromotionMustBeSequential);
            }

            var unmet = UnmetPromotionCriteria(current, performance, controlViolations, policy);
            if (unmet.Count > 0)
            {
                return new StageTransitionResult(
                    Accepted: false, Transition: null, ResultingSettings: null, RejectionReasons: unmet,
                    Stage1Criteria: criteria);
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
            RejectionReasons: [],
            Stage1Criteria: criteria);
    }

    // FR-20, ADR-0008: 撤退（差し戻し）基準の評価。到達時は自動停止（HaltNewEntries）と降格提案（ProposedStage）を返す。
    // Stage 2/3: 実DD ≥ バックテスト最大DD × 倍率 で自動停止＋Stage 0 再検証提案。Stage 1: 乖離が説明不能で Stage 0 差し戻し提案。
    // 段階の実降格は提案に留め、確定は承認付き RequestTransition を要する（自動＝停止、承認＝段階変更）。
    public static WithdrawalAssessment AssessWithdrawal(
        TradingStage current, StagePerformance performance, StageGatePolicy policy)
    {
        switch (current)
        {
            // FR-20, #333, §4.3, INDEX 決定 42: 累計 120 営業日を経ても取引件数が 100 件に届かなければ
            // **Stage 1 を打ち切り Stage 0 へ差し戻す**。件数不足は「サンプルが足りない」ではなく
            // 「戦略が想定した頻度で発火していない」ことの兆候であり、延長ではなく設計の見直しが正しい対処である。
            // SIMULATE のため実弾の即時停止は不要（HaltNewEntries: false）。段階の実降格は提案に留める。
            //
            // 旧・機械判定の撤退事由「ペーパー段階の乖離が説明不能」は**計画 §4 が機械判定から外した**
            // （月報の三者比較を利用者が読んで判断する）。よって Stage 1 の機械判定の撤退はこの 1 事由だけである。
            case TradingStage.Stage1Simulate
                when Stage1Gate.Evaluate(performance.Stage1Progress, policy.Stage1Criteria)
                    == Stage1GateOutcome.Exhausted:
                return new WithdrawalAssessment(
                    Triggered: true,
                    Reason: WithdrawalReason.Stage1ExtensionExhausted,
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
        TradingStage current,
        StagePerformance performance,
        ControlViolationTally? controlViolations,
        StageGatePolicy policy)
    {
        var unmet = new List<StageGateCriterion>();
        switch (current)
        {
            case TradingStage.Stage0Verification:
                // FR-15: DSR 補正後もエッジが正・最大DD ≤ 10%（バックテスト合格。ADR-0018 決定2）
                if (!performance.BacktestPassed)
                {
                    unmet.Add(StageGateCriterion.BacktestNotPassed);
                }

                break;

            case TradingStage.Stage1Simulate:
                // FR-20, #333, 06_daytrading-review §4.1: 機械判定の 3 条件。
                //   条件 1 統制違反 0 件（**クラス C 限定**。RejectionReasonClassification が分類の単一情報源）
                //   条件 2 実際に取引できた日数が 60 営業日（§4.2 の期間カウント規則）
                //   条件 3 取引件数 100 件（§4.1）
                // **条件 2 と条件 3 は両方を満たすまで昇格しない**（§4.3・INDEX 決定 42）。
                // 条件 4・5（ZDR の有効化・信用取引の必要額）は「作業が完了しているか」の
                // **昇格時チェックリスト**であり機械判定ではない（§4.1）。ここでは評価しない。
                // FR-20, #387, IADR-0148: 条件 1 は「集計が供給されているか」と「0 件か」の 2 段で判定する。
                // **未供給を 0 件と同一視しない。** 段階ゲートの他の入力（営業日数・取引件数）の 0 は
                // 「未充足＝昇格しない」に倒れるが、違反件数の 0 だけは「合格」を意味する。
                // 供給元が無いまま 0 を合格と読むと、#385 / #386 が期間・件数を供給した瞬間に
                // 条件 1 が無条件で通る（本判定が塞いだ fail-open）。
                if (controlViolations is null)
                {
                    unmet.Add(StageGateCriterion.ControlViolationCountUnavailable);
                }
                else if (controlViolations.BlocksPromotion)
                {
                    unmet.Add(StageGateCriterion.ControlViolationsPresent);
                }

                var progress = performance.Stage1Progress;
                var criteria = policy.Stage1Criteria;
                if (progress.QualifiedTradingDays < criteria.TargetTradingDays)
                {
                    unmet.Add(StageGateCriterion.Stage1TradingDaysInsufficient);
                }

                if (progress.TradeCount < criteria.MinimumTradeCount)
                {
                    unmet.Add(StageGateCriterion.Stage1TradeCountInsufficient);
                }

                // 打ち切り（累計 120 営業日を経ても件数不足）は昇格の否定として明示的に列挙する。
                // 件数不足の理由（Stage1TradeCountInsufficient）と併記されるが、両者は意味が違う——
                // 前者は「まだ足りない」、後者は「もう延長しない」である。監査で区別できることに実益がある。
                if (Stage1Gate.Evaluate(progress, criteria) == Stage1GateOutcome.Exhausted)
                {
                    unmet.Add(StageGateCriterion.Stage1ExtensionExhausted);
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

    // 次段階＝現段階の 1 段上（最上段なら null）。TradingStage は連続昇順の数値で固定されている前提に依存する
    // （StageSettings.cs のコメント参照。値の挿入・並べ替えは禁止）。
    private static TradingStage? NextStage(TradingStage stage) =>
        stage == TradingStage.Stage3ScaledLive ? null : (TradingStage)((int)stage + 1);

    // #466, IADR-0180: 拒否時も**実効の合格条件を必ず載せる**。引数で強制することで、
    // 新しい拒否経路を足したときに載せ忘れをコンパイルが止める（`params` は最後に置く必要がある）。
    private static StageTransitionResult Reject(
        Stage1GateCriteria criteria, params StageGateCriterion[] reasons) =>
        new(Accepted: false, Transition: null, ResultingSettings: null, RejectionReasons: reasons,
            Stage1Criteria: criteria);
}
