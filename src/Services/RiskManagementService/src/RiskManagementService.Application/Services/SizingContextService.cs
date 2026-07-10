using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.State;

namespace AiStockTrading.RiskManagement.Application.Services;

// FR-04, FR-10, IADR-0029: 取引判断へ供給するサイジング文脈を、設定（IRiskSettingsStore）とポートフォリオ状態
// （PortfolioSnapshotBuilder＝#63 台帳の実データ）から導出する。取引判断は本文脈を同期 API で照会する。
public sealed class SizingContextService(PortfolioSnapshotBuilder snapshotBuilder, IRiskSettingsStore settingsStore)
{
    public SizingContextView Build()
    {
        var snapshot = snapshotBuilder.Build();
        var settings = settingsStore.GetCurrent();

        // 段階資金残枠＝CapitalCap − 取得額累計（IADR-0005）。日次発注残枠＝1日上限 − 当日発注累計。いずれも負値は 0。
        var stageRemaining = Math.Max(0m, settings.Stage.CapitalCap - snapshot.InvestedCapital);
        var dailyRemaining = Math.Max(0m, settings.Limits.MaxDailyOrderAmount - snapshot.DailyOrderedAmount);

        return new SizingContextView(
            Capital: snapshot.Capital,
            StageCapitalRemaining: stageRemaining,
            DailyOrderRemaining: dailyRemaining,
            ConsecutiveLosses: snapshot.ConsecutiveLosses,
            DrawdownRatio: snapshot.DrawdownRatio,
            Mode: settings.Stage.Mode,
            Limits: settings.Limits);
    }
}
