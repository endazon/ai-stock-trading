extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

// FR-04, FR-10, FR-15, #1308, IADR-0528 決定6: Stage 0 の記録のサイジング文脈（固定値）。
//
// 当時の残枠・連敗・DD は再構成できない。記録は保有なしで固定し（IADR-0351）、数量は再生で再計算しない（IADR-0318）。
// そこで、再生の始まり（初期資金・保有なし・当日未発注）と同じ状態を固定値で与える。
//   - 資金は再生の初期資金（BacktestService の Stage0EvaluationService.InitialCapital）と同じ。数量と再生の資金の尺度を揃える。
//   - 統制値は計画の既定値（TradingDefaults。シミュレータのプロファイルも比率は同じ）。
//   - 損切りの実行機構は未供給（null）。当時の設定を再構成しない（プロンプトは「不明」と書く）。
public static class Stage0AsOfSizing
{
    /// <summary>再生の初期資金と同じ値（BacktestService の Stage0EvaluationService.InitialCapital）。</summary>
    public const decimal Capital = 1_000_000m;

    public static SizingContext Create()
    {
        var limits = TradingDefaults.CreateRiskLimits();
        return new SizingContext(
            Capital: Capital,
            StageCapitalRemaining: Capital,
            DailyOrderRemaining: limits.MaxDailyOrderAmountFor(Capital),
            ConsecutiveLosses: 0,
            DrawdownRatio: 0m,
            Mode: BrokerProvider.MoomooSimulate,
            Limits: limits);
    }
}
