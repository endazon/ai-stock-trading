using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.State;

namespace AiStockTrading.RiskManagement.Application.Services;

// FR-10, UC-07, ADR-0009: `/status`（詳細設計07）が参照する稼働状態を集約する。表示専用（副作用なし）。
// 3 統制（kill switch / 日次損失ロックアウト / 一時停止）の現在状態・優先順位・段階・当日損益・上限使用率・
// ポジションを 1 回の一貫スナップショットに束ねる。優先順位は表示用で、新規建て停止の判定は OR。
public sealed class RiskStatusService(
    PortfolioSnapshotBuilder snapshotBuilder,
    IRiskSettingsStore settingsStore,
    IPauseStore pauseStore,
    ILockoutStore lockoutStore,
    IStageGateStore stageGateStore,
    IClock clock)
{
    public RiskStatusView Build()
    {
        var snapshot = snapshotBuilder.Build();
        var settings = settingsStore.GetCurrent();
        var pause = pauseStore.GetState();
        var stage = stageGateStore.Load().CurrentStage;

        // 日次損失ロックアウトは「当日有効か」で判定する（翌営業日の解除日 ReleaseOn 未満なら有効）。
        // 表示専用のため状態の掃除（Clear）は行わない（掃除は発注審査経路の責務・OrderScreeningService）。
        var lockout = lockoutStore.Get();
        var lockoutActive = lockout is not null && lockout.IsActiveOn(clock.Today);

        // 優先順位（ADR-0009・重い順）: kill switch > 日次損失ロックアウト > 一時停止。表示の見出し用。
        var activeControl = snapshot.KillSwitchEngaged
            ? ActiveTradingControl.KillSwitch
            : lockoutActive
                ? ActiveTradingControl.DailyLossLockout
                : pause.Paused
                    ? ActiveTradingControl.Pause
                    : ActiveTradingControl.None;

        // 新規建て停止は 3 統制の OR。手仕舞い・損切りは本フラグに関わらず継続する。
        var newEntriesBlocked = snapshot.KillSwitchEngaged || lockoutActive || pause.Paused;

        return new RiskStatusView(
            KillSwitchEngaged: snapshot.KillSwitchEngaged,
            DailyLossLockoutActive: lockoutActive,
            LockoutReleaseOn: lockout?.ReleaseOn,
            TradingPaused: pause.Paused,
            ActiveControl: activeControl,
            NewEntriesBlocked: newEntriesBlocked,
            Stage: stage,
            DailyRealizedPnl: snapshot.DailyRealizedPnl,
            UnrealizedPnl: snapshot.UnrealizedPnl,
            DailyPnl: snapshot.DailyRealizedPnl + snapshot.UnrealizedPnl,
            Capital: snapshot.Capital,
            DailyOrderedAmount: snapshot.DailyOrderedAmount,
            MaxDailyOrderAmount: settings.Limits.MaxDailyOrderAmount,
            DrawdownRatio: snapshot.DrawdownRatio,
            MaxDrawdownRatio: settings.Limits.MaxDrawdownRatio,
            OpenPositionCount: snapshot.OpenPositionCount,
            MaxOpenPositions: settings.Limits.MaxOpenPositions);
    }
}
