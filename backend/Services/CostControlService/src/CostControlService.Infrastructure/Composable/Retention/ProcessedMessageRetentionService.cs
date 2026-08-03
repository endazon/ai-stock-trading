using AiStockTrading.CostControl.Application.Ports;
using AiStockTrading.Shared.Contracts.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiStockTrading.CostControl.Infrastructure.Composable.Retention;

// NFR（運用）, #137, IADR-0059: processed_messages（重複排除ストア）の保持期間パージ。
// 追記専用で無期限に肥大化するのを止める。境界は RetentionPolicy が決め、cutoff より新しい行には触れない
// （触れると LlmCostIncurred の再配信で二重計上する＝IADR-0055 決定5 が壊れる）。
//
// 既定は無効（IADR-0059 決定4）。有効化は appsettings / Helm values の `Retention:Enabled=true`。
internal sealed class ProcessedMessageRetentionService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IOptions<RetentionOptions> options,
    ILogger<ProcessedMessageRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            // fail-safe: 明示的に有効化されるまで 1 行も消さない。
            logger.LogInformation(
                "processed_messages の保持期間パージは無効です（Retention:Enabled=false）。行は保持され続けます。");
            return;
        }

        logger.LogInformation(
            "processed_messages の保持期間パージを開始します（保持 {Days} 日・間隔 {Interval}）。",
            RetentionPolicy.EffectiveRetentionDays(options.Value.RetentionDays),
            options.Value.Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PurgeOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // フェイルセーフ: パージの失敗で費用統制サービスを止めない（次回巡回で再試行する）。
                logger.LogError(ex, "processed_messages のパージに失敗しました。次回巡回で再試行します。");
            }

            try
            {
                await Task.Delay(options.Value.Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // 1 巡回。削除件数を返す（単体テスト可能な単位として公開する）。
    public Task<int> PurgeOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cutoff = RetentionPolicy.CutoffFor(clock.UtcNow, options.Value.RetentionDays);

        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IProcessedMessageStore>();

        var purged = store.PurgeProcessedBefore(cutoff, options.Value.EffectiveBatchSize);

        if (purged > 0)
            logger.LogInformation(
                "processed_messages を {Count} 件パージしました（{Cutoff} より古い行）。", purged, cutoff);

        return Task.FromResult(purged);
    }
}
