using AiStockTrading.Shared.Contracts.Trading;
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
public sealed class MonitorPollingService(
    IServiceScopeFactory scopeFactory,
    IMarketSchedule schedule,
    IClock clock,
    IOptions<MonitorOptions> options,
    ILogger<MonitorPollingService> logger,
    StopLossLivenessReporter? liveness = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.PollIntervalSeconds));
        using var timer = new PeriodicTimer(interval);

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

        using var scope = scopeFactory.CreateScope();
        var monitor = scope.ServiceProvider.GetRequiredService<AppSvc>();
        // ADR-0013, IADR-0129, #354: 発行は Wolverine の IMessageBus（scoped）。巡回ごとのスコープから解決する
        // （Wolverine の PublishAsync は CancellationToken を取らない。巡回の中断は上位のループが見る）。
        var publish = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var result = await monitor.EvaluateRoundAsync(cancellationToken).ConfigureAwait(false);

        // 損切りを先に発行する（フェイルセーフ・損切り優先。IADR-0014）。
        foreach (var stopLoss in result.StopLosses)
        {
            await publish.PublishAsync(stopLoss).ConfigureAwait(false);
        }

        foreach (var movement in result.PriceMovements)
        {
            await publish.PublishAsync(movement).ConfigureAwait(false);
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
