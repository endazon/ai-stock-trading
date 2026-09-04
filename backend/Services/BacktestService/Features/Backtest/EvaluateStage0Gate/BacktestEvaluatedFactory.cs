using BacktestService.Domain;
using AiStockTrading.Shared.Contracts.Events;

namespace BacktestService.Features.Backtest.EvaluateStage0Gate;

// FR-15, FR-20, FR-11, IADR-0089: Stage 0 合格判定（Stage0Decision）を Risk へ供給する契約イベント
// （BacktestEvaluated）へ写す純関数。発行側（BacktestService）が自分の verdict の契約表現を所有する。
// バス発行の実駆動（IPublishEndpoint）は BacktestService の go-live ホスト（#82 系）で結線し、本 mapper は
// 配線の発行側を単体テストで担保する。
//
// FR-20, ADR-0016 決定14, #388, IADR-0304: 「空売りを含む戦略か」は**申告ではなく観測**である。
// 呼び出し元（発行ホスト）は走行（BacktestRun）を渡すだけであり、真偽値を名乗る口は無い。
//
// backtestMaxDrawdownRatio と evaluatedAt は Stage0Decision が保持しないため呼び出し元（発行ホスト）が渡す。
// go-live ホストは backtestMaxDrawdownRatio を、評価に用いた同一 Stage0GateContext.BaselineMetrics.MaxDrawdown
// から導出すること（Stage 0 判定と供給値の乖離を避けるための発行側の契約・IADR-0089）。
public static class BacktestEvaluatedFactory
{
    /// <param name="run">
    /// FR-20, ADR-0016 決定14, #388, IADR-0304: 評価した**走行そのもの**。
    /// 「空売りを含む戦略か」は**この走行の約定列から観測する**（<see cref="ShortSellingObservation"/>）。
    /// <para>
    /// 🔴 **真偽値で申告する引数は置かない。** 置けば発行ホストは渡し違えられ、**一度も空売りをしていない
    /// 戦略の Stage 0 合格で実弾の空売りが解禁され得る**（#388 が最重要とした否定形を、呼び出し元の
    /// 正直さだけで守ることになる）。計画（ADR-0016 決定14）は「含む」の判定方法を定めていないため、
    /// **保守的な側（観測）**を採った（IADR-0304 決定1・環流 planning#534）。
    /// </para>
    /// </param>
    /// <param name="strategyId">
    /// 評価した戦略の識別子。verdict の無効化契機「戦略の変更」を機械判定する鍵であり、
    /// **戦略を変えたら変わる値**を渡すこと（同じ値のまま中身を変えると、古い verdict が生き残る）。
    /// </param>
    public static BacktestEvaluated From(
        Stage0Decision decision,
        decimal backtestMaxDrawdownRatio,
        DateTimeOffset evaluatedAt,
        BacktestRun run,
        string strategyId)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(decision.Gate);
        ArgumentNullException.ThrowIfNull(run);

        return new BacktestEvaluated(
            Passed: decision.Gate.Passed,
            MaxDrawdownRatio: backtestMaxDrawdownRatio,
            DeflatedSharpe: decision.DeflatedSharpe,
            ProbabilityOfBacktestOverfitting: decision.ProbabilityOfBacktestOverfitting,
            // 未達条件は Risk 側の監査・診断のため名称の連結で持つ（合格なら空文字）。ドメインの単一情報源を共有する。
            FailedChecks: decision.Gate.FormatFailedChecks(),
            EvaluatedAt: evaluatedAt,
            IncludesShortSelling: ShortSellingObservation.Includes(run.Fills),
            StrategyId: strategyId ?? string.Empty);
    }
}
