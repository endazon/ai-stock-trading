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
    StageGatePolicy policy,
    KillSwitchService killSwitch,
    IClock clock)
{
    // FR-20, UC-06: 現況＝現段階・その設定・遷移履歴（監査）・昇格評価・撤退評価。
    public StageGateStatus GetStatus()
    {
        var ledger = ledgerStore.Load();
        var performance = performanceStore.GetCurrent();
        var current = ledger.CurrentStage;
        return new StageGateStatus(
            current,
            policy.SettingsFor(current),
            ledger.History,
            StageGate.AssessPromotion(current, performance),
            StageGate.AssessWithdrawal(current, performance, policy));
    }

    // FR-20: 遷移履歴（追記順・監査対象）。
    public IReadOnlyList<StageTransition> GetHistory() => ledgerStore.Load().History;

    // FR-20, UC-06: 承認による段階遷移。承認者が空なら純ドメインが拒否する（承認なしに遷移しない）。
    // 受理時のみ台帳へ追記する。昇格は合格基準充足を要し、差し戻し（降格方向）は安全側のため承認のみで受理する。
    public StageTransitionResult RequestTransition(TradingStage target, string approver)
    {
        var ledger = ledgerStore.Load();
        var performance = performanceStore.GetCurrent();
        var approval = new StageApproval(target, approver);

        var result = StageGate.RequestTransition(
            ledger.CurrentStage, ledger.NextSequence, approval, performance, policy, clock.UtcNow);

        if (result is { Accepted: true, Transition: not null })
        {
            ledgerStore.Append(result.Transition);
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
        var performance = performanceStore.GetCurrent();
        var assessment = StageGate.AssessWithdrawal(ledger.CurrentStage, performance, policy);

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
