using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Application.Services;

// FR-10, FR-19, FR-20, UC-01, UC-02, ADR-0003: 取引判断（TradeDecisionMade）を発注前に決定的に検証し、
// OrderApproved / OrderRejected を生成する。判定コア RiskEvaluator（ステートレス）に、ホストが保持する
// ロックアウト状態（IADR-0008）を合成する。日次損失上限の新規到達でロックアウトを設定し、含み損が回復しても
// 当日中（翌営業日の解除まで）は新規建てを止め続ける。手仕舞い（Close）はフェイルセーフで常に通す。
public sealed class OrderScreeningService(
    IRiskSettingsStore settingsStore,
    PortfolioSnapshotBuilder snapshotBuilder,
    ILockoutStore lockoutStore,
    IClock clock,
    IBusinessCalendar businessCalendar,
    IManipulativeOrderPatternDetector? patternDetector = null)
{
    public ScreeningOutcome Screen(TradeDecisionMade decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var intent = decision.Intent;
        var settings = settingsStore.GetCurrent();
        var snapshot = snapshotBuilder.Build();
        var isEntry = intent.PositionEffect == PositionEffect.Open;

        // 判定コア（決定的）を実行し、違反理由を集約する。
        var result = RiskEvaluator.Evaluate(intent, settings, snapshot, patternDetector);
        var reasons = new List<RejectionReason>(result.Reasons);

        // 日次損失上限に「新規到達」したら当日ロックアウトを設定する（翌営業日まで）。
        // RiskEvaluator は実現+含み損の合算で当該時点の到達を判定する（IADR-0008）。
        if (reasons.Contains(RejectionReason.DailyLossLimitReached))
        {
            EngageLockout();
        }
        else if (isEntry && IsLockedOut())
        {
            // ロックアウトは当日中維持する。含み損が回復して RiskEvaluator が到達と判定しなくても、
            // 一度到達した当日は翌営業日の解除まで新規建てを拒否し続ける（デイリーストップの趣旨）。
            reasons.Add(RejectionReason.DailyLossLimitReached);
        }

        if (reasons.Count > 0)
        {
            return ScreeningOutcome.Reject(new OrderRejected(
                decision.DecisionId, intent, reasons, clock.UtcNow));
        }

        return ScreeningOutcome.Approve(new OrderApproved(
            decision.DecisionId, intent, result.ApprovedQuantity, clock.UtcNow));
    }

    private bool IsLockedOut()
    {
        var lockout = lockoutStore.Get();
        if (lockout is null)
        {
            return false;
        }

        // 翌営業日の解除日に達していれば失効させ、状態を掃除する。
        if (!lockout.IsActiveOn(clock.Today))
        {
            lockoutStore.Clear();
            return false;
        }

        return true;
    }

    private void EngageLockout()
    {
        var existing = lockoutStore.Get();
        // 既に当日有効なロックアウトがあれば解除日を延長しない（同日中の重複到達で解除が先送りされるのを防ぐ）。
        if (existing is not null && existing.IsActiveOn(clock.Today))
        {
            return;
        }

        var releaseOn = businessCalendar.NextBusinessDay(clock.Today);
        lockoutStore.Set(new LockoutState(
            releaseOn,
            "日次損失上限到達により当日ロックアウト（翌営業日まで）",
            clock.UtcNow));
    }
}
