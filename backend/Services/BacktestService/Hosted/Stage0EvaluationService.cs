using BacktestService.Domain;
using BacktestService.Features.Backtest;
using BacktestService.Features.Backtest.EvaluateStage0Gate;
using BacktestService.Features.Backtest.RunBacktest;
using BacktestService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Kernel.Trading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;

namespace BacktestService.Hosted;

// FR-15, FR-20, ADR-0008, ADR-0033, #688, IADR-0310: Stage 0 判定の定時駆動。
// 過去データを 1 回だけ取得し、評価して verdict（BacktestEvaluated）を発行する。受け側はリスク管理の
// 段階別実績（IStagePerformanceStore）であり、そこが Stage 0→1 昇格ゲートの入力になる（IADR-0089）。
//
// 🔴 **既定は無効である**（Backtest:Stage0:Enabled。IADR-0310 決定1）。無効なら巡回もバー取得も publish も起きない。
//
// 🔴 **評価対象は構成 `Backtest:Stage0:Strategy` で選ぶ**（#632, IADR-0318 決定3）。
//   - `placeholder`（**既定**）: IADR-0310 のまま。verdict は不合格固定で、go-live の判断材料にならない。
//   - `recorded-replay`: ADR-0033 の記録・再生。**記録が構成と整合するときだけ**本物の Stage0GateService へ進む。
//     整合しなければ判定を走らせず、理由（NoDecisionRecords / RecordingMismatch / DataCutoff /
//     InsufficientEvaluationSample）を載せた不合格 verdict を出す。
// **どちらの経路にも合格を作る口は無い**——合格を出せるのは Stage0GateService（7 条件）だけである。
public sealed class Stage0EvaluationService(
    IServiceScopeFactory scopeFactory,
    IOptions<Stage0EvaluationOptions> options,
    TimeProvider timeProvider,
    ILogger<Stage0EvaluationService> logger) : BackgroundService
{
    // シミュレーションの初期資金と費用モデル。前提条件の既定値をそのまま用いる（独自の数値を発明しない）。
    // #632, IADR-0318: 記録再生（本番戦略）の走行も同じ費用式を使う——判断時見積り・事後集計・バックテストで
    // 費用式を分けない（FR-17 の単一情報源）。
    private const decimal InitialCapital = 1_000_000m;

    private static BacktestCostModel CostModel() =>
        new(TradingAssumptionsDefaults.Create(), SlippageRatio: 0m);

    // プレースホルダ走行のシミュレーション設定。**判定に使わない**（verdict は不合格固定）。
    private static BacktestConfig PlaceholderConfig() =>
        new(InitialCapital, CostModel(), CostSensitivity.Baseline);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            // fail-safe: 明示的に有効化するまで 1 度も走らない（外部への要求も verdict の発行も起きない）。
            logger.LogInformation(
                "Stage 0 判定の定時駆動は無効です（既定）。有効化するには {Section}:Enabled=true を設定してください。",
                Stage0EvaluationOptions.SectionName);
            return;
        }

        using var timer = new PeriodicTimer(options.Value.EffectiveInterval(), timeProvider);

        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break; // 停止要求
            }
            catch (Exception ex)
            {
                // フェイルセーフ: 1 巡回の失敗（取得の一時エラー・発行の一時エラー等）で常駐を止めない。
                logger.LogError(ex, "Stage 0 判定の巡回でエラーが発生しました。次回巡回を継続します。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 1 巡回。過去データを取得し、verdict を組んで発行する。**単体テスト可能な単位として公開する**
    /// （`Enabled` の判定は <see cref="ExecuteAsync"/> が持つ。本メソッドは呼ばれたら必ず 1 通発行する）。
    /// </summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var (from, to) = settings.EvaluationWindow(DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime));

        using var scope = scopeFactory.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<IHistoricalBarSource>();
        // ADR-0013, IADR-0129: 発行は Wolverine の IMessageBus（scoped）。巡回ごとのスコープから解決する。
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        // 取得はシミュレーションの外側で 1 回だけ行い、結果をスナップショットへ固定する（決定性の保全・IADR-0105）。
        var snapshot = await MaterializedBarDataSource
            .LoadAsync(source, settings.ToUniverse(), from, to, cancellationToken)
            .ConfigureAwait(false);
        var bars = snapshot.GetBars(from, to);

        BacktestEvaluated evaluated;
        if (bars.Count == 0)
        {
            evaluated = EmptyBarVerdict(from, to, snapshot);
        }
        else if (settings.ResolveStrategy() == Stage0EvaluationOptions.RecordedReplayStrategyName)
        {
            // FR-04, ADR-0033 決定1/決定2, #632, IADR-0318: 評価対象は AI 判断の記録・再生である。
            var records = await scope.ServiceProvider
                .GetRequiredService<IStage0DecisionRecordSource>()
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            evaluated = RecordedReplayVerdict(settings, from, to, snapshot, records);
        }
        else
        {
            if (settings.HasUnknownStrategy())
            {
                // 綴り違いで本番戦略が黙って走らない（＝毎日プレースホルダの不合格が出続ける）ことを可視化する。
                logger.LogWarning(
                    "Stage 0: 未知の戦略名 {Strategy} が構成されています。既定（{Default}）で走行します。",
                    settings.Strategy, Stage0EvaluationOptions.PlaceholderStrategyName);
            }

            evaluated = PlaceholderVerdict(settings, from, to, snapshot, bars);
        }

        await bus.PublishAsync(evaluated).ConfigureAwait(false);
    }

    // 🔴 FR-15, IADR-0310 決定2（最重要の否定形）: **バーが 1 本も無いときは判定を走らせない。**
    //
    // DataCutoffPolicy は空バーを違反と見なさない（`bars.All(...)` は空に対して真）。判定器を通すと
    // 「検証条件①だけは満たしている」verdict が出て、7 条件の残りが別経路で埋まった瞬間に合格し得る。
    // 判定の手前で断ち、**理由を NoHistoricalBars として明示した不合格**を発行する。
    private BacktestEvaluated EmptyBarVerdict(DateOnly from, DateOnly to, MaterializedBarDataSource snapshot)
    {
        logger.LogWarning(
            "Stage 0: 期間 {From}〜{To} の過去データが 0 本のため**判定を行いません**（欠測 {Gaps} 件）。"
            + "不合格の verdict を発行します（合格 verdict は出しません）。",
            from, to, snapshot.Gaps.Count);

        // 走行が無いため空の走行を渡す（約定 0＝空売りの観測も false）。最大 DD は 0（評価していない）。
        var emptyRun = new BacktestRun([], [], [], BacktestMetricsCalculator.Compute([], []), UnfilledOrderCount: 0);
        return Publishable(Stage0DriverVerdict.NoHistoricalBars(), backtestMaxDrawdownRatio: 0m, emptyRun);
    }

    // FR-15, FR-20, ADR-0033, IADR-0310 決定3: バーがあってもプレースホルダ戦略の走行は**不合格固定**である。
    // 走行そのものは行う（駆動経路＝取得→シミュレーション→写像→発行が実際に動くことの確認）。
    private BacktestEvaluated PlaceholderVerdict(
        Stage0EvaluationOptions settings,
        DateOnly from,
        DateOnly to,
        MaterializedBarDataSource snapshot,
        IReadOnlyList<PriceBar> bars)
    {
        var run = new BacktestRunner(snapshot).Run(
            new BacktestRequest(settings.ToUniverse(), from, to, new PlaceholderStrategy(), PlaceholderConfig()));

        // ADR-0033 決定3: 汚染対策はカットオフ後データを原則とする。**カットオフ日が未構成なら未充足**へ倒す。
        var cutoff = settings.ParseLlmTrainingCutoff();
        var cutoffSatisfied = cutoff is not null && DataCutoffPolicy.IsAllAfterCutoff(bars, cutoff.Value);

        logger.LogInformation(
            "Stage 0: 期間 {From}〜{To} のバー {Bars} 本（欠測 {Gaps} 件）でプレースホルダ戦略を走行しました。"
            + "**本番の合否判定ではありません**（不合格固定の verdict を発行します）。",
            from, to, bars.Count, snapshot.Gaps.Count);

        // IADR-0089: backtestMaxDrawdownRatio は評価に用いた同一走行の最大 DD から導出する（乖離させない）。
        return Publishable(Stage0DriverVerdict.PlaceholderRun(cutoffSatisfied), run.Metrics.MaxDrawdown, run);
    }

    // FR-04, FR-15, FR-20, ADR-0033 決定1/決定2/決定3, #632, IADR-0318: **本番戦略（AI 判断の記録・再生）の評価。**
    //
    // 🔴 **否定形（最重要）**: 記録が無い／構成と整合しない／カットオフ日が未構成・不一致／標本不足 の
    // いずれかなら、**本物の判定器を呼ばずに不合格固定へ倒す**。整合しない記録で判定を通すと、
    // 「別の期間・別の銘柄・別の汚染対策前提で採った判断」の成績が Stage 0 の合否として記録され得る。
    //
    // 整合するときだけ Stage0GateService（DSR/PBO・コスト 2 倍・ウォークフォワード・試行数・カットオフの
    // 7 条件）へ進む。**合否はそこが決める**——駆動は合格を作る口を一切持たない。
    private BacktestEvaluated RecordedReplayVerdict(
        Stage0EvaluationOptions settings,
        DateOnly from,
        DateOnly to,
        MaterializedBarDataSource snapshot,
        Stage0DecisionRecordSet? records)
    {
        var preparation = Stage0ReplayEvaluation.Prepare(new Stage0ReplayEvaluationRequest(
            RecordSet: records,
            DataSource: snapshot,
            Universe: settings.ToUniverse(),
            From: from,
            To: to,
            LlmTrainingCutoff: settings.ParseLlmTrainingCutoff(),
            InitialCapital: InitialCapital,
            CostModel: CostModel(),
            Criteria: Stage0GateCriteria.Default));

        if (!preparation.IsReady)
        {
            logger.LogWarning(
                "Stage 0: 記録再生の評価文脈を組めないため**判定を行いません**（理由 {Reasons}・期間 {From}〜{To}）。"
                + "不合格の verdict を発行します（合格 verdict は出しません）。",
                string.Join(", ", preparation.BlockingChecks), from, to);

            // 走行できた分（標本不足のとき）は最大 DD と空売り観測に使う。走行できていなければ空の走行を渡す。
            var run = preparation.BaselineRun
                ?? new BacktestRun([], [], [], BacktestMetricsCalculator.Compute([], []), UnfilledOrderCount: 0);
            return BacktestEvaluatedFactory.From(
                Stage0DriverVerdict.RecordingUnusable(preparation.BlockingChecks),
                run.Metrics.MaxDrawdown,
                timeProvider.GetUtcNow(),
                run,
                // 記録が読めていれば戦略 ID を名乗る（読めていなければ空＝「戦略が無い」）。
                preparation.StrategyId);
        }

        var decision = new Stage0GateService().Evaluate(preparation.GateContext!);
        var baseline = preparation.BaselineRun!;

        logger.LogInformation(
            "Stage 0: 記録再生戦略 {StrategyId} を評価しました（期間 {From}〜{To}・欠測 {Gaps} 件・"
            + "合格 {Passed}・未達 {Failed}）。",
            preparation.StrategyId, from, to, snapshot.Gaps.Count, decision.Gate.Passed,
            decision.Gate.FormatFailedChecks());

        // IADR-0089: backtestMaxDrawdownRatio は評価に用いた同一走行の最大 DD から導出する（乖離させない）。
        // IADR-0304: 「空売りを含む戦略か」は同じ走行の約定列から観測する（申告させない）。
        return BacktestEvaluatedFactory.From(
            decision, baseline.Metrics.MaxDrawdown, timeProvider.GetUtcNow(), baseline, preparation.StrategyId);
    }

    private BacktestEvaluated Publishable(Stage0Decision decision, decimal backtestMaxDrawdownRatio, BacktestRun run) =>
        BacktestEvaluatedFactory.From(
            decision,
            backtestMaxDrawdownRatio,
            timeProvider.GetUtcNow(),
            run,
            PlaceholderStrategy.StrategyId);
}
