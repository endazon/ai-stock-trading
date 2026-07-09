using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Domain;

namespace AiStockTrading.RiskManagement.Application.Services;

// FR-10, IADR-0005, IADR-0008: 判定入力 PortfolioSnapshot（Domain・非永続の派生データ）を組み立てる。
// 生の運用状態（IPortfolioStateProvider）に kill switch 状態（IKillSwitchStore）を合成する。
// InvestedCapital（取得額合計）・UnrealizedPnl（含み損益）はプロバイダが供給した実値をそのまま反映する。
public sealed class PortfolioSnapshotBuilder(
    IPortfolioStateProvider stateProvider,
    IKillSwitchStore killSwitchStore)
{
    public PortfolioSnapshot Build()
    {
        var state = stateProvider.GetCurrent();
        var killSwitch = killSwitchStore.GetState();

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
            KillSwitchEngaged = killSwitch.Engaged,
        };
    }
}
