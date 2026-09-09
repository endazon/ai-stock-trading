using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Trading;
using BacktestService.Domain;
using BacktestService.Features.Backtest.RunBacktest;

namespace BacktestService.Features.Backtest.EvaluateStage0Gate;

// FR-04, FR-15, FR-20, ADR-0008, ADR-0033 決定2/決定3, #632, IADR-0318:
// **記録再生戦略に対する Stage 0 評価文脈の組み立て**（純関数）。
//
// ADR-0033 決定2 は「シミュレーション・ウォークフォワード・コスト 2 倍感度・DSR/PBO をすべて決定的に回す」
// と定めた。本型はその 4 つの入力を 1 つの記録集合から組み、`Stage0GateService` へ渡せる
// `Stage0GateContext` を作る。**合否判定そのものは行わない**（判定器は Stage0GateService が単一情報源）。
//
// 🔴 **整合しない記録では判定を組まない。** 記録が無い・期間が評価期間を覆っていない・銘柄集合が違う・
// カットオフ日が違う／未構成、のいずれかなら `BlockingChecks` を返して**判定器を呼ばせない**
// （IADR-0310 決定2 が空バーに対して置いた fail-closed と同じ向き）。

/// <summary>評価文脈を組むための入力。</summary>
/// <param name="LlmTrainingCutoff">
/// 構成の LLM 学習カットオフ日。**null（未構成）は判定を組まない理由になる**（ADR-0033 決定3。
/// カットオフ日の供給元は計画側に未登録であり、未構成を充足へ倒さない）。
/// </param>
public sealed record Stage0ReplayEvaluationRequest(
    Stage0DecisionRecordSet? RecordSet,
    IBarDataSource DataSource,
    SecurityUniverse Universe,
    DateOnly From,
    DateOnly To,
    DateOnly? LlmTrainingCutoff,
    decimal InitialCapital,
    BacktestCostModel CostModel,
    Stage0GateCriteria Criteria);

/// <summary>
/// 組み立て結果。<see cref="BlockingChecks"/> が空でなければ判定器へ進んではならない。
/// <see cref="BaselineRun"/> は走行できた場合にだけ入る（走行できた分は verdict の最大 DD・空売り観測に使う）。
/// </summary>
public sealed record Stage0ReplayPreparation(
    IReadOnlyList<Stage0GateCheck> BlockingChecks,
    string StrategyId,
    BacktestRun? BaselineRun,
    Stage0GateContext? GateContext)
{
    public bool IsReady => BlockingChecks.Count == 0 && BaselineRun is not null && GateContext is not null;
}

public static class Stage0ReplayEvaluation
{
    /// <summary>
    /// FR-15, ADR-0008: PBO（CSCV）の分割数。`ProbabilityOfBacktestOverfitting` は偶数・2 以上・
    /// ブロック数以下を要求する。4 は「日次リターンが 4 本以上あれば組める」最小の実用値であり、
    /// 組合せ数 C(4,2)=6 で決定的に回る。
    /// </summary>
    public const int OverfittingPartitions = 4;

    /// <summary>
    /// FR-15, ADR-0008: ウォークフォワードの IS:OOS 比（2:1）。
    /// <para>
    /// 🔴 **記録再生戦略はパラメータ探索を持たない。** したがって IS 区間の役割は「最適化する」ことではなく、
    /// OOS 区間を「記録の後半だけで確かめる」ために切り分けることに尽きる。比率を構成へ出さないのは、
    /// **探索が実装されるまで運用が調整できる意味を持たない**ためである（探索が入る時点で構成化する）。
    /// </para>
    /// </summary>
    private const int InSampleNumerator = 2;
    private const int InSampleDenominator = 3;

    public static Stage0ReplayPreparation Prepare(Stage0ReplayEvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.DataSource);
        ArgumentNullException.ThrowIfNull(request.Universe);
        ArgumentNullException.ThrowIfNull(request.CostModel);
        ArgumentNullException.ThrowIfNull(request.Criteria);

        var recordSet = request.RecordSet;
        if (recordSet is null || recordSet.Records is null || recordSet.Records.Count == 0)
        {
            // 評価対象が存在しない。既定の供給ポート（NoStage0DecisionRecordSource）では常にこの経路である。
            return new Stage0ReplayPreparation([Stage0GateCheck.NoDecisionRecords], string.Empty, null, null);
        }

        var blocking = Validate(recordSet, request);
        if (blocking.Count > 0)
            return new Stage0ReplayPreparation(blocking, recordSet.StrategyId, null, null);

        var strategy = new RecordedDecisionReplayStrategy(recordSet);
        var baseline = Run(request, strategy, CostSensitivity.Baseline, request.From, request.To);
        var doubled = Run(request, strategy, CostSensitivity.Doubled, request.From, request.To);

        // 過剰適合補正の標本は日次リターンである。分割数に満たなければ PBO は組めない。
        // 🔴 **足りないまま組むと「過剰適合が無い」ように見えるだけ**なので、判定の手前で断つ。
        var dailyReturns = baseline.Metrics.DailyReturns;
        if (dailyReturns.Count < OverfittingPartitions)
        {
            return new Stage0ReplayPreparation(
                [Stage0GateCheck.InsufficientEvaluationSample], recordSet.StrategyId, baseline, null);
        }

        var walkForwardReturn = WalkForwardOutOfSampleReturn(request, strategy);

        // 試行台帳は **1 本**（記録そのもの）である。記録再生戦略はパラメータ探索を持たないため
        // 「何回試したか」は 1 回であり、`MinTrials`（既定 20）を満たさない＝現時点の合否は必ず不合格になる。
        // これは仕様である —— 探索を経ずに合格させれば、DSR の多重検定補正が恒等的に消える（IADR-0110 の実測）。
        var trials = new TrialLedger();
        trials.Record(new BacktestTrial(
            recordSet.StrategyId, baseline.Metrics.SharpeRatio, (double)walkForwardReturn));

        var context = new Stage0GateContext(
            BaselineMetrics: baseline.Metrics,
            DoubledCostTotalReturn: doubled.Metrics.TotalReturn,
            Trials: trials,
            OverfittingPerformanceMatrix: OverfittingMatrix(dailyReturns),
            OverfittingPartitions: OverfittingPartitions,
            WalkForwardOutOfSampleReturn: walkForwardReturn,
            Bars: request.DataSource.GetBars(request.From, request.To),
            LlmTrainingCutoff: request.LlmTrainingCutoff!.Value,
            Criteria: request.Criteria,
            // ADR-0033 決定3: 汚染対策はカットオフ後データを原則とし、**匿名化は合否判定の根拠に用いない**。
            // ここを true にできる口を作らない（作れば匿名化経路で合格が出る）。
            DataAnonymized: false);

        return new Stage0ReplayPreparation([], recordSet.StrategyId, baseline, context);
    }

    /// <summary>
    /// FR-15, ADR-0033 決定2/決定3: 記録が評価の構成と整合するかを検査する（純関数・単体で検証できる形にする）。
    /// </summary>
    public static IReadOnlyList<Stage0GateCheck> Validate(
        Stage0DecisionRecordSet recordSet, Stage0ReplayEvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(recordSet);
        ArgumentNullException.ThrowIfNull(request);

        var blocking = new List<Stage0GateCheck>();

        // カットオフ日が未構成なら評価しない（ADR-0033 決定3。未構成を「充足」へ倒さない）。
        if (request.LlmTrainingCutoff is null)
            blocking.Add(Stage0GateCheck.DataCutoff);

        var mismatch =
            // 記録が評価期間を覆っていない（覆っていない区間は「判断していない」＝無発注になり、
            // 何もしなかった成績を AI 判断の成績として読むことになる）。
            recordSet.From > request.From
            || recordSet.To < request.To
            // 記録のカットオフ日が構成と違う（別の汚染対策前提で採った記録を流用させない）。
            || (request.LlmTrainingCutoff is { } cutoff && recordSet.LlmTrainingCutoff != cutoff)
            // 銘柄集合が違う（評価ユニバースと記録対象がずれると、生存者バイアス排除の前提が崩れる）。
            || !SameSymbols(recordSet, request);

        if (mismatch)
            blocking.Add(Stage0GateCheck.RecordingMismatch);

        return blocking;
    }

    private static bool SameSymbols(Stage0DecisionRecordSet recordSet, Stage0ReplayEvaluationRequest request)
    {
        var configured = request.Universe.MembersBetween(request.From, request.To).ToHashSet();
        var recorded = (recordSet.Symbols ?? [])
            .Select(s => (s.Symbol, s.Market))
            .ToHashSet();
        return configured.SetEquals(recorded);
    }

    private static BacktestRun Run(
        Stage0ReplayEvaluationRequest request,
        IBacktestStrategy strategy,
        CostSensitivity sensitivity,
        DateOnly from,
        DateOnly to) =>
        new BacktestRunner(request.DataSource).Run(new BacktestRequest(
            request.Universe, from, to, strategy,
            new BacktestConfig(request.InitialCapital, request.CostModel, sensitivity)));

    // FR-15, ADR-0008: ウォークフォワードの OOS リターン。記録期間を 2:1 で切り、**OOS 区間のバーだけで
    // 建玉ゼロから再走行**した総リターンを返す（IS 区間の建玉を持ち越さない＝OOS の成績に IS が混ざらない）。
    // 窓が 1 つも取れない短い期間では 0 を返し、ウォークフォワード条件（> 0）を満たさない側へ倒す。
    private static decimal WalkForwardOutOfSampleReturn(
        Stage0ReplayEvaluationRequest request, IBacktestStrategy strategy)
    {
        var totalDays = request.To.DayNumber - request.From.DayNumber + 1;
        if (totalDays < 2)
            return 0m;

        var inSampleDays = Math.Max(1, totalDays * InSampleNumerator / InSampleDenominator);
        var outOfSampleDays = Math.Max(1, totalDays - inSampleDays);

        var windows = WalkForwardSplitter.Rolling(request.From, request.To, inSampleDays, outOfSampleDays);
        if (windows.Count == 0)
            return 0m;

        // 窓が複数取れる場合は、各 OOS 区間の総リターンの平均を採る（1 つの窓に依存させない）。
        var returns = windows
            .Select(w => Run(request, strategy, CostSensitivity.Baseline, w.OutOfSampleStart, w.OutOfSampleEnd)
                .Metrics.TotalReturn)
            .ToList();
        return returns.Sum() / returns.Count;
    }

    // FR-15, ADR-0008, 06_daytrading-review §3.2: CSCV の性能行列。
    //
    // 🔴 **戦略候補は「記録した AI 判断」と「何もしない」の 2 本である。** CSCV は複数の戦略候補の
    // 順位付けから過剰適合を推定する手法であり、候補が 1 本では成立しない（実装も 2 本以上を要求する）。
    // 記録再生方式には探索の候補群が無いため、意味のある比較対象は**エッジの有無**しかない。
    // 「何もしない」の各ブロック成績は定義から 0 であり、走行して求める必要がない。
    // 得られる PBO は「IS で現金に勝った記録が、OOS でも現金に勝つか」の割合であり、保守的な読みができる。
    private static double[][] OverfittingMatrix(IReadOnlyList<double> dailyReturns) =>
        [.. dailyReturns.Select(r => new[] { r, 0d })];
}
