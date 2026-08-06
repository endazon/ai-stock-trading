using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Domain;

namespace AiStockTrading.RiskManagement.Application.Services;

// FR-10, IADR-0005, IADR-0008, ADR-0009: 判定入力 PortfolioSnapshot（Domain・非永続の派生データ）を組み立てる。
// 生の運用状態（IPortfolioStateProvider）に kill switch 状態（IKillSwitchStore）と一時停止状態（IPauseStore）を合成する。
// InvestedCapital（取得額合計）・UnrealizedPnl（含み損益）はプロバイダが供給した実値をそのまま反映する。
//
// FR-19, #375, ADR-0021 決定3, IADR-0153: あわせてブローカーへ照会した口座種別の観測を合成する。
// **未供給・失効は null のまま渡す**（判定コアが新規建てを止める）。ここで既定値を補ってはならない。
public sealed class PortfolioSnapshotBuilder(
    IPortfolioStateProvider stateProvider,
    IKillSwitchStore killSwitchStore,
    IPauseStore pauseStore,
    IBrokerAccountObservationStore accountObservations)
{
    public PortfolioSnapshot Build()
    {
        var state = stateProvider.GetCurrent();
        var killSwitch = killSwitchStore.GetState();
        var pause = pauseStore.GetState();

        return new PortfolioSnapshot
        {
            Capital = state.Capital,
            OpenPositionCount = state.OpenPositionCount,
            InvestedCapital = state.InvestedCapital,
            DailyOrderedAmount = state.DailyOrderedAmount,
            DailyRealizedPnl = state.DailyRealizedPnl,
            UnrealizedPnl = state.UnrealizedPnl,
            DrawdownRatio = state.DrawdownRatio,
            ConsecutiveLosses = state.ConsecutiveLosses,
            SymbolsTradedToday = state.SymbolsTradedToday,
            Account = accountObservations.GetCurrent(),
            KillSwitchEngaged = killSwitch.Engaged,
            TradingPaused = pause.Paused,
        };
    }
}
