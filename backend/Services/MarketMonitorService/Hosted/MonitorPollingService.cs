using MarketMonitorService.Common.Abstractions;
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
    ILogger<MonitorPollingService> logger) : BackgroundService
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
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!schedule.IsOpen(clock.UtcNow))
        {
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
    }
}
