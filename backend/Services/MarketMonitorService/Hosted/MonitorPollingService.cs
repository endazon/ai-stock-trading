using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using MarketMonitorService.Common.Abstractions;
using MarketMonitorService.Domain;
using MarketMonitorService.Features.MarketMonitor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;
using AppSvc = MarketMonitorService.Features.MarketMonitor.MarketMonitorAppService;

namespace MarketMonitorService.Hosted;

// FR-03, UC-02, ADR-0003: 監視間隔ごとのポーリング。市場開場時に 1 巡回評価し、検知イベント（損切り・変動）を発行する。
// 閉場中はスキップ（監視停止）。個々の巡回の例外は監視を止めないよう握りつぶしてログする（フェイルセーフ）。
// EvaluateRoundAsync は scoped な EF ストアに依存するため、巡回ごとに DI スコープを作る。
//
// FR-04, NFR-01, ADR-0043, #1251, IADR-0513: 開場して評価した巡回の所要を計量（ast.market_monitor.cycle_duration_seconds）し、
// 巡回間隔 ＋ 余裕（Finnhub の 1 要求ぶんの送出間隔。#1281）を超えたら Warning を出す（観測のみ。巡回の挙動は変えない）。
// 経過は TimeProvider で測る（試験は偽の時計で進める）。
public sealed class MonitorPollingService(
    IServiceScopeFactory scopeFactory,
    IMarketSchedule schedule,
    IClock clock,
    IOptions<MonitorOptions> options,
    ILogger<MonitorPollingService> logger,
    StopLossLivenessReporter? liveness = null,
    FinnhubDailyVolumeRecorder? dailyVolume = null,
    BusinessMetrics? metrics = null,
    TimeProvider? timeProvider = null,
    CycleOverrunTolerance? overrunTolerance = null) : BackgroundService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    // FR-10, #1280, IADR-0520: 発行した到達の記憶（巡回をまたぐ。本サービスは singleton で巡回は直列）。
    private readonly StopLossArrivalGate _arrivals = new();

    // 巡回間隔（ExecuteAsync の PeriodicTimer と同じ値。1 未満は 1 秒）。
    private TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(1, options.Value.PollIntervalSeconds));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

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
                // フェイルセーフ: 1 巡回の失敗（価格取得・発行の一時エラー等）で監視を止めない。
                logger.LogError(ex, "市場監視の巡回でエラーが発生しました。次回巡回を継続します。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    // 1 巡回。市場開場時のみ評価・発行する。単体テスト可能な単位として公開する。
    //
    // #909, IADR-0380 決定2: 開場判定は**市場ごと**である。どの市場も開いていない巡回は何もしない（従来どおり）。
    // 1 つでも開いていれば巡回し、閉場している市場の銘柄は評価の中で飛ばす（MarketMonitorAppService）。
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var startedAt = _time.GetTimestamp();
        var now = clock.UtcNow;
        var markets = Enum.GetValues<Market>();
        var closedMarkets = Array.FindAll(markets, m => !schedule.IsOpen(m, now));

        if (closedMarkets.Length == markets.Length)
        {
            // #902, IADR-0365 決定4: 閉場中は評価しないので、欠落の起点を持ち越さない（#904 監査 N2）。
            // #909, IADR-0380 決定3: あわせて、保護が働かないことを閉場ごとに 1 回だけ声に出す。
            ReportClosedMarkets(closedMarkets, [], now);
            return; // 閉場中は監視停止（04_workflows/02）
        }

        // FR-04, NFR-01, ADR-0043, #1251, IADR-0513: 例外で抜けた巡回も所要を数える（遅い失敗も次の刻みを遅らせる）。
        // 停止要求で**中断した**巡回（取り消し済みのトークンで OperationCanceledException）だけを除く（途中で切った所要は巡回の所要ではない）。
        // トークンの状態だけで判定すると、停止の直前に最後まで回った巡回まで落とす（PR #1266 の AI レビュー 🟢）。
        var canceled = false;
        try
        {
            await RunOpenCycleAsync(closedMarkets, markets, now, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            canceled = true;
            throw;
        }
        finally
        {
            if (!canceled)
                ObserveCycleDuration(_time.GetElapsedTime(startedAt));
        }
    }

    // 開場している市場が 1 つ以上ある巡回の本体（評価・発行・観測）。
    private async Task RunOpenCycleAsync(
        Market[] closedMarkets, Market[] markets, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var monitor = scope.ServiceProvider.GetRequiredService<AppSvc>();
        // ADR-0013, IADR-0129, #354: 発行は Wolverine の IMessageBus（scoped）。巡回ごとのスコープから解決する
        // （Wolverine の PublishAsync は CancellationToken を取らない。巡回の中断は上位のループが見る）。
        var publish = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        // 損切りを先に発行する（フェイルセーフ・損切り優先。IADR-0014）。
        // 🔴 FR-10, #1282, IADR-0520: 到達は検知した時点で発行する（巡回の末尾までためない）。
        // 🔴 FR-10, #1280, IADR-0520: 同じ到達は 1 回だけ発行する（決済が台帳へ反映されるまでの巡回で再発行しない）。
        var result = await monitor.EvaluateRoundAsync(
            (stopLoss, ct) => PublishStopLossAsync(publish, stopLoss, ct), cancellationToken).ConfigureAwait(false);
        _arrivals.Settle(result);

        foreach (var movement in result.PriceMovements)
        {
            await publish.PublishAsync(movement).ConfigureAwait(false);
        }

        // FR-01, ADR-0031（計画）決定2〜4, ADR-0043（計画）決定 3, #1132, IADR-0477: 日次要求見積りを巡回ごとに、
        // この巡回の照会の対象（保有と監視銘柄の和集合。#1189, IADR-0494）の実数から記録する（是正前は起動時に運用者の申告 1 銘柄で数えていた）。
        // 観測のみ。失敗しても巡回を失敗させない（発行の後に置く）。
        if (dailyVolume is not null)
        {
            try
            {
                dailyVolume.Record(result.QuotedSymbolMarkets, options.Value.PollIntervalSeconds);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Finnhub の日次要求見積りの記録に失敗しました（監視・発行には影響しません）。");
            }
        }

        // FR-10, #902, IADR-0365 決定4: 評価の生存要約・価格欠落の Warning（観測のみ）。発行の**後**に置き、
        // 要約の失敗は巡回を失敗させない（到達の発行・監視の継続に一切影響させない）。
        if (liveness is not null)
        {
            try
            {
                // #909, IADR-0380 決定3: **閉場の報告を先に出す。** 保有 0 件の Observe は観測状態を捨てるため、
                // 後に置くと「引け際の最終観測値」が消えてから報告することになる。
                ReportClosedMarkets(closedMarkets, result.ClosedMarketPositions, now);
                foreach (var market in markets.Except(closedMarkets))
                    liveness.OnMarketOpen(market); // 監査 F3: 次の閉場期間でまた「閉場と判定」を出せるようにする
                liveness.Observe(result.StopLossEvaluations, now);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "損切り評価の生存要約の記録に失敗しました（監視・発行には影響しません）。");
            }
        }
    }

    // FR-10, FR-03, #1280, #1282, IADR-0520: 検知した到達を 1 件発行する。同じ到達を発行済みなら抑止する（INF で残す）。
    // 発行の失敗は他の保有の評価を止めない（LogError して記憶しない＝次の巡回で発行し直す）。停止要求だけは伝える。
    private async Task PublishStopLossAsync(IMessageBus publish, StopLossTriggered stopLoss, CancellationToken cancellationToken)
    {
        if (!_arrivals.ShouldPublish(stopLoss))
        {
            logger.LogInformation(
                "損切りライン到達の再発行を抑止しました（同じ到達を発行済み・価格は前回の発行と同じか有利・{RepublishAfter} 以内）: {Symbol}/{Market} ライン={StopLoss} 検知価格={Price} 検知時刻={DetectedAt:O}",
                StopLossArrivalGate.RepublishAfter, stopLoss.Symbol, stopLoss.Market, stopLoss.StopLossPrice, stopLoss.Price, stopLoss.DetectedAt);
            return;
        }

        try
        {
            await publish.PublishAsync(stopLoss).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                ex,
                "損切りライン到達の発行に失敗しました（次の巡回で発行し直します）: {Symbol}/{Market} ライン={StopLoss} 検知価格={Price}",
                stopLoss.Symbol, stopLoss.Market, stopLoss.StopLossPrice, stopLoss.Price);
            return;
        }

        _arrivals.MarkPublished(stopLoss);
        logger.LogInformation(
            "損切りライン到達を発行しました: {Symbol}/{Market} ライン={StopLoss} 検知価格={Price} 検知時刻={DetectedAt:O} EventId={EventId}",
            stopLoss.Symbol, stopLoss.Market, stopLoss.StopLossPrice, stopLoss.Price, stopLoss.DetectedAt, stopLoss.EventId);
    }

    // FR-04, NFR-01, ADR-0043 決定 2 (b), #1251, IADR-0513: 1 巡回の所要を計量し、巡回間隔 ＋ 余裕を超えたら Warning を出す。
    // #1281: 間隔「以上」で鳴らすと、(b) が「収まる」とした境界の構成（所要 ≒ 間隔で回る）で毎巡回鳴る。厳密な超過で、余裕を足して判定する。
    // PeriodicTimer は逃した刻みを 1 つに畳むため、達した巡回の次は待たずに始まり、1 銘柄あたりの価格の確認の周期が間隔を超える。
    // 観測のみ。失敗しても巡回を失敗させない（生存の報告と同じ作法）。
    private void ObserveCycleDuration(TimeSpan elapsed)
    {
        try
        {
            metrics?.RecordMarketMonitorCycleDuration(elapsed.TotalSeconds);

            var interval = Interval;
            var tolerance = (overrunTolerance ?? CycleOverrunTolerance.None).Value;
            if (elapsed > interval + tolerance)
            {
                logger.LogWarning(
                    "市場監視の 1 巡回の所要 {ElapsedSeconds:F1} 秒が巡回間隔 {IntervalSeconds} 秒と余裕 {ToleranceSeconds:0.###} 秒の和を超えました。"
                        + "次の巡回は待たずに始まり、1 銘柄あたりの価格の確認の周期が巡回間隔を超えます（損切りの検知が遅れます）。"
                        + "保有の照会・Finnhub の往復・発行の所要と、1 巡回の照会の数（自制レート × 巡回間隔の内側か）を確認してください。",
                    elapsed.TotalSeconds, interval.TotalSeconds, tolerance.TotalSeconds);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "市場監視の巡回の所要の記録に失敗しました（監視・発行には影響しません）。");
        }
    }

    // #909, IADR-0380 決定3: 閉場している市場ごとに、保護の空白を 1 回だけ報告する。
    // 次の開場時刻はカレンダーに尋ねる（見通せなければ null のまま渡し、報告側が時刻を伏せる）。
    private void ReportClosedMarkets(
        IReadOnlyList<Market> closedMarkets, IReadOnlyList<StopLossEvaluation> closedPositions, DateTimeOffset now)
    {
        if (liveness is null)
            return;

        foreach (var market in closedMarkets)
        {
            liveness.OnMarketClosed(
                market,
                closedPositions.Where(p => p.Market == market).ToArray(),
                now,
                schedule.NextOpen(market, now));
        }
    }
}
