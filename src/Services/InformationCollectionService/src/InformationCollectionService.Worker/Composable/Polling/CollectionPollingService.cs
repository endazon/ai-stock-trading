using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AiStockTrading.Shared.Contracts.Events;
using AppSvc = AiStockTrading.InformationCollection.Application.Services.InformationCollectionService;

namespace AiStockTrading.InformationCollection.Worker.Composable.Polling;

// FR-01, FR-02, UC-01: 収集間隔ごとのポーリング。1 巡回で収集→正規化→サニタイズ→KB 保存し、収集があれば InformationCollected
// を発行して取引サイクル（FR-02）の起点にする。巡回の例外は握りつぶしてログする（フェイルセーフ・収集を止めない）。
internal sealed class CollectionPollingService(
    IServiceScopeFactory scopeFactory,
    IOptions<CollectionOptions> options,
    ILogger<CollectionPollingService> logger) : BackgroundService
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
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "情報収集の巡回でエラーが発生しました。次回巡回を継続します。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    // 1 巡回。単体テスト可能な単位として公開する。
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<AppSvc>();
        var publish = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        var result = await collector.CollectAsync(cancellationToken).ConfigureAwait(false);

        // 収集があった場合のみ取引サイクルの起点イベントを発行する（空巡回では起動しない）。
        if (result.ItemCount > 0)
        {
            await publish.Publish(
                new InformationCollected(Guid.NewGuid(), result.ItemCount, DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
