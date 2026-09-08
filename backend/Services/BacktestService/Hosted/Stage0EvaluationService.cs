using BacktestService.Domain;
using BacktestService.Features.Backtest;
using BacktestService.Features.Backtest.EvaluateStage0Gate;
using BacktestService.Features.Backtest.RunBacktest;
using BacktestService.Infrastructure.ExternalServices;
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
// 🔴 **現時点の verdict は必ず不合格である**（IADR-0310 決定3）。評価対象が本番戦略ではなく
// プレースホルダ（ADR-0033 の記録・再生は未実装）であるため、Stage0DriverVerdict が不合格固定で組む。
// **経路は通るが go-live の判断材料にはならない。**
public sealed class Stage0EvaluationService(
    IServiceScopeFactory scopeFactory,
    IOptions<Stage0EvaluationOptions> options,
    TimeProvider timeProvider,
    ILogger<Stage0EvaluationService> logger) : BackgroundService
{
    // プレースホルダ走行のシミュレーション設定。**判定に使わない**（verdict は不合格固定）ため、
    // 費用・初期資金は前提条件の既定値をそのまま用いる（独自の数値を発明しない）。
    private static readonly BacktestConfig PlaceholderConfig = new(
        InitialCapital: 1_000_000m,
        CostModel: new BacktestCostModel(TradingAssumptionsDefaults.Create(), SlippageRatio: 0m),
        Sensitivity: CostSensitivity.Baseline);

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

        var evaluated = bars.Count == 0
            ? EmptyBarVerdict(from, to, snapshot)
            : PlaceholderVerdict(settings, from, to, snapshot, bars);

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
            new BacktestRequest(settings.ToUniverse(), from, to, new PlaceholderStrategy(), PlaceholderConfig));

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

    private BacktestEvaluated Publishable(Stage0Decision decision, decimal backtestMaxDrawdownRatio, BacktestRun run) =>
        BacktestEvaluatedFactory.From(
            decision,
            backtestMaxDrawdownRatio,
            timeProvider.GetUtcNow(),
            run,
            PlaceholderStrategy.StrategyId);
}
