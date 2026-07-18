using AiStockTrading.Backtest.Domain;
using AiStockTrading.Shared.Contracts.Events;

namespace AiStockTrading.Backtest.Application;

// FR-15, FR-20, FR-11, IADR-0089: Stage 0 合格判定（Stage0Decision）を Risk へ供給する契約イベント
// （BacktestEvaluated）へ写す純関数。発行側（BacktestService）が自分の verdict の契約表現を所有する。
// バス発行の実駆動（IPublishEndpoint）は BacktestService の go-live ホスト（#82 系）で結線し、本 mapper は
// 配線の発行側を単体テストで担保する。
//
// backtestMaxDrawdownRatio と evaluatedAt は Stage0Decision が保持しないため呼び出し元（発行ホスト）が渡す。
// go-live ホストは backtestMaxDrawdownRatio を、評価に用いた同一 Stage0GateContext.BaselineMetrics.MaxDrawdown
// から導出すること（Stage 0 判定と供給値の乖離を避けるための発行側の契約・IADR-0089）。
public static class BacktestEvaluatedFactory
{
    public static BacktestEvaluated From(
        Stage0Decision decision, decimal backtestMaxDrawdownRatio, DateTimeOffset evaluatedAt)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(decision.Gate);

        return new BacktestEvaluated(
            Passed: decision.Gate.Passed,
            MaxDrawdownRatio: backtestMaxDrawdownRatio,
            DeflatedSharpe: decision.DeflatedSharpe,
            ProbabilityOfBacktestOverfitting: decision.ProbabilityOfBacktestOverfitting,
            // 未達条件は Risk 側の監査・診断のため名称の連結で持つ（合格なら空文字）。ドメインの単一情報源を共有する。
            FailedChecks: decision.Gate.FormatFailedChecks(),
            EvaluatedAt: evaluatedAt);
    }
}
