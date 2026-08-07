using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;

namespace AiStockTrading.RiskManagement.Application.Services;

// FR-20, FR-15, UC-06, ADR-0008, IADR-0041/0070: 段階ゲートの遷移管理（承認による昇格・差し戻し）を運用系へ結線する。
// 純ドメイン StageGate（承認ゲート・撤退評価）を、永続化台帳（IStageGateStore）と段階別実績（IStagePerformanceStore）、
// 段階ゲート方針（StageGatePolicy）へ束ねる。承認者は認証済み利用者名（ホスト層 OwnerOnly で担保）。
// 「承認なしに段階が遷移しない」は RequestTransition の空承認拒否で構造的に保証する。
public sealed class StageGateService(
    IStageGateStore ledgerStore,
    IStagePerformanceStore performanceStore,
    IControlViolationObservationStore violationStore,
    IStage1FillObservationStore fillStore,
    IStage1TradingDayObservationStore tradingDayStore,
    StageGatePolicy policy,
    IRiskSettingsStore settingsStore,
    KillSwitchService killSwitch,
    IClock clock)
{
    // FR-20, FR-13, SC-02, #423, §4.1 条件 3, IADR-0164 決定4: **最小取引件数だけが設定値である。**
    //
    // `StageGatePolicy` は DI の singleton であり、段階別の資金上限・撤退倍率という「運用中に変わらない
    // 方針」を表す。設定値（監査・楽観排他の対象）をそこへ混ぜず、**判定の直前に重ねる**。
    //
    // **重ねるのは MinimumTradeCount だけである。** 目標営業日数（60）と打ち切り（120）は
    // 裁定が「及ばない」「変わらない」と明示した項目であり、設定から書き換える経路を作らない。
    private StageGatePolicy EffectivePolicy() => policy with
    {
        Stage1Criteria = policy.Stage1Criteria with
        {
            MinimumTradeCount = settingsStore.GetCurrent().Stage1MinimumTradeCount,
        },
    };

    // FR-20, #387, IADR-0148: 統制違反件数（§4.1 条件1）は段階別実績の行ではなく**発注審査の観測ログ**から集計する。
    // **null＝未供給**であり「違反 0 件」ではない。判定関数の必須引数であるため、渡し忘れはコンパイルが止める。
    private ControlViolationTally? CurrentTally() => violationStore.GetTally();

    // FR-20, #386, §4.1 条件3, IADR-0149 決定3: 取引件数（§4.1 条件3）も段階別実績の行ではなく
    // **約定の観測ログ**から集計し、判定の直前に段階別実績へ重ねる。
    // 実績行に列として持たせないのは、供給元が 2 つになれば必ず食い違うためである
    // （観測ログが計上単位を担保する唯一の場所である）。
    // 統制違反件数（#387）と違い**未供給を 0 と区別しない**——取引件数の 0 は「条件未充足＝昇格しない」に
    // 倒れる fail-safe であり、区別に意味が無い。
    //
    // FR-20, #385, §4.2, IADR-0150 決定4: 営業日数（§4.2 の期間カウント）も同じ形で**稼働の観測ログ**から集計する。
    // 実績行の列は削除済みであり、供給元は 1 つ（stage1_session_uptime）だけである。
    // paper 稼働で除外された日数（SC-03 の併記）も同じ観測から出る——別の供給に分けると、
    // 「経過 42 日（paper 稼働により 3 日を除外）」という説明の 2 つの数が食い違い得る。
    private StagePerformance CurrentPerformance() =>
        performanceStore.GetCurrent() with
        {
            Stage1TradeCount = fillStore.GetTradeCount(),
            Stage1QualifiedTradingDays = tradingDayStore.GetQualifiedTradingDayCount(),
            Stage1ExcludedInternalPaperDays = tradingDayStore.GetExcludedInternalPaperDayCount(),
        };

    // FR-20, UC-06: 現況＝現段階・その設定・遷移履歴（監査）・昇格評価・撤退評価。
    public StageGateStatus GetStatus()
    {
        var ledger = ledgerStore.Load();
        var performance = CurrentPerformance();
        var current = ledger.CurrentStage;
        var effective = EffectivePolicy();
        return new StageGateStatus(
            current,
            effective.SettingsFor(current),
            ledger.History,
            StageGate.AssessPromotion(current, performance, CurrentTally(), effective),
            StageGate.AssessWithdrawal(current, performance, effective),
            performance.Stage1Progress,
            // SC-02, SC-03, #423: **実効の合格条件**を返す（設定で変わった件数を含む）。
            // `BelowStatisticalBasis`（100 件未満か）もここに載り、画面と Discord はこの宣言に従う。
            effective.Stage1Criteria);
    }

    // FR-20: 遷移履歴（追記順・監査対象）。
    public IReadOnlyList<StageTransition> GetHistory() => ledgerStore.Load().History;

    // FR-20, UC-06: 承認による段階遷移。承認者が空なら純ドメインが拒否する（承認なしに遷移しない）。
    // 受理時のみ台帳へ追記する。昇格は合格基準充足を要し、差し戻し（降格方向）は安全側のため承認のみで受理する。
    public StageTransitionResult RequestTransition(TradingStage target, string approver)
    {
        var ledger = ledgerStore.Load();
        var performance = CurrentPerformance();
        var approval = new StageApproval(target, approver);

        var result = StageGate.RequestTransition(
            ledger.CurrentStage, ledger.NextSequence, approval, performance, CurrentTally(),
            EffectivePolicy(), clock.UtcNow);

        if (result is { Accepted: true, Transition: not null })
        {
            ledgerStore.Append(result.Transition);

            // FR-20, ADR-0008, IADR-0103, #164: 受理された差し戻し（降格）で実DD の観測窓を区切る。
            // 実DD は単調非減少で累積するため、リセット経路が無いと「撤退 → 降格 → 再昇格」の直後に過去の実DD で
            // 撤退が恒久的に再発火する。差し戻しは再検証のやり直しであり、観測もそこで区切るのが ADR-0008 の意図に合う。
            // 昇格側ではリセットしない（撤退の証拠を消さない＝厳しい側）。台帳追記の後（＝遷移確定後）に行い、
            // 実DD 以外のフィールドは温存する（フィールド所有権の分離・IADR-0089 の鏡像）。
            // 既に 0（未供給・リセット済み）なら書き込まない＝未記録の実績行を差し戻しだけで作らない。
            if (result.Transition.Kind == StageTransitionKind.Demotion
                && performance.ObservedMaxDrawdownRatio != 0m)
            {
                performanceStore.Save(StagePerformanceProjection.WithoutObservedDrawdown(performance));
            }

            // FR-20, #387, §4.1 条件1, IADR-0148 決定4: 受理された遷移で統制違反の観測窓を区切る。
            // 計画は「集計期間は **Stage 1 の全期間**」と定める。窓を段階遷移で区切ると、Stage 1 に居る間の窓は
            // 「Stage 1 へ入った時点から現在まで」＝計画の期間そのものになる。
            // **実DD のリセット（差し戻しのみ）と条件が違うのは意図的である**——実DD は「撤退の証拠を消さない」ため
            // 昇格では区切らないが、統制違反は前段階（例: Stage 0）の記録を Stage 1 の合格証跡へ持ち込んでは
            // ならないため昇格でも区切る。区切った直後は未供給＝昇格しない（fail-safe）。
            violationStore.ResetWindow();

            // FR-20, #386, §4.2「起算点＝Stage 1 遷移日」, IADR-0149 決定3: 受理された遷移で約定の観測窓も区切る。
            // 統制違反の窓（#387）と同条件にするのは、3 指標が同じ「Stage 1 の期間」を指す必要があるためである。
            // 区切った直後は 0 件＝昇格しない（fail-safe）。
            fillStore.ResetWindow();

            // FR-20, #385, §4.2「起算点＝Stage 1 遷移日」, IADR-0150 決定4: 稼働の観測窓も同条件で区切る。
            // 3 指標（統制違反・取引件数・営業日数）が同じ「Stage 1 の期間」を指す必要がある。
            // 区切った直後は 0 日＝昇格しない（fail-safe）。
            tradingDayStore.ResetWindow();
        }

        return result;
    }

    // FR-20, ADR-0008, IADR-0083: 撤退基準を評価し、到達時は自動で安全側に倒す（IADR-0041「自動＝停止・承認＝段階変更」）。
    // HaltNewEntries（Stage 2/3 で実DD がバックテスト最大DD × 倍率超）なら kill switch を自動起動する。
    // 段階の実降格は行わず ProposedStage を返すにとどめ、確定は承認付き RequestTransition を要する。
    // この呼び出しで新規に起動したか（NewlyEngaged）を戻り値に含める。定期評価ドライバ（#166）は本フラグで
    // 「新規停止時のみ 1 回通知」を判定し、呼び出し側の snapshot 比較（check-then-act）に依存しない。
    public WithdrawalEvaluationOutcome EvaluateWithdrawal()
    {
        var ledger = ledgerStore.Load();
        // FR-20, #386, §4.3, IADR-0149 決定3: **撤退評価も取引件数を要する**（Stage 1 の打ち切り＝累計 120 営業日を
        // 経ても 100 件に届かない）。実績行は件数を持たないため、ここでも観測ログの値を重ねる。
        // 重ね忘れると件数が常に 0 に見え、**件数に達していても打ち切りが提案され続ける**（誤った差し戻し）。
        var performance = CurrentPerformance();
        // #423: 撤退（§4.3 の打ち切り）も**実効の件数**で評価する。打ち切りの規則（累計 120 営業日）は
        // 変えないが、「120 営業日を経ても**設定した件数**に届かない」という判定でなければ、
        // 件数を上げた直後に打ち切りが出ない／下げた直後に出続けるという食い違いが生じる。
        var assessment = StageGate.AssessWithdrawal(ledger.CurrentStage, performance, EffectivePolicy());

        var newlyEngaged = false;
        if (assessment is { Triggered: true, HaltNewEntries: true } && !killSwitch.GetState().Engaged)
        {
            killSwitch.Engage(
                "system:stage-gate-withdrawal",
                $"撤退基準到達（{assessment.Reason}）により自動停止（安全側・FR-20/ADR-0008）");
            newlyEngaged = true;
        }

        return new WithdrawalEvaluationOutcome(assessment, newlyEngaged);
    }
}
