using RiskManagementService.Application.Adapters;
using RiskManagementService.Application.Ports;
using RiskManagementService.Application.State;

namespace RiskManagementService.Application.Services;

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
        // #337（#249 吸収）, IADR-0246: 表示は市場を特定できないため、**最も遅れている市場の現地取引日**で
        // 判定する（保守側）。JST（先行側）で判定すると、米国セッションではまだ効いている統制を
        // 「解除済み」と表示してしまう。発注審査の実判定は注文の市場ごと（OrderScreeningService）。
        var lockout = lockoutStore.Get();
        var lockoutActive = lockout is not null && lockout.IsActiveOn(TradingDay.EarliestCurrent(clock.UtcNow));

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
            // FR-20, #334: 発注先は段階と独立の軸であり、設定から読む（段階からは導出しない）。
            BrokerProvider: settings.BrokerProvider,
            DailyRealizedPnl: snapshot.DailyRealizedPnl,
            UnrealizedPnl: snapshot.UnrealizedPnl,
            DailyPnl: snapshot.DailyRealizedPnl + snapshot.UnrealizedPnl,
            Capital: snapshot.Capital,
            DailyOrderedAmount: snapshot.DailyOrderedAmount,
            // FR-10, #329, IADR-0130 決定1: 上限は equity 比のため、表示も equity から解決した実額を載せる。
            MaxOrderAmount: settings.Limits.MaxOrderAmountFor(snapshot.Capital),
            MaxDailyOrderAmount: settings.Limits.MaxDailyOrderAmountFor(snapshot.Capital),
            DrawdownRatio: snapshot.DrawdownRatio,
            MaxDrawdownRatio: settings.Limits.MaxDrawdownRatio,
            OpenPositionCount: snapshot.OpenPositionCount,
            MaxOpenPositions: settings.Limits.MaxOpenPositions);
    }
}
