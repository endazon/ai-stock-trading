using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.Shared.Contracts.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiStockTrading.OrderExecution.Infrastructure.Composable.Retention;

// NFR（運用）, #137, FR-05, IADR-0059: order_dispatch_reservations の **終端行のみ** の保持期間パージ。
//
// パージ対象は Completed かつ CompletedAt が cutoff より古い行に限る。**Reserved は対象外**であり、
// どれだけ古くても消さない（Reserved＝ブローカへ発注済みか不明＝消せば再配送で二重発注・実弾では実損）。
// 滞留 Reserved の解消は #141 の自動リコンサイルか人手の判断であって、時間経過ではない（IADR-0059 決定1）。
//
// 既定は無効（IADR-0059 決定4）。有効化は appsettings / Helm values の `Retention:Enabled=true`。
internal sealed class OrderReservationRetentionService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IOptions<RetentionOptions> options,
    ILogger<OrderReservationRetentionService> logger) : BackgroundService
{
    // NFR-08, NFR-09, #339: 本サービスが消すストア。RetentionScope の宣言と照合する（7 年保持側を指したら落ちる）。
    private const string PurgeTarget = "order_dispatch_reservations";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            // fail-safe: 明示的に有効化されるまで 1 行も消さない。
            logger.LogInformation(
                "order_dispatch_reservations の保持期間パージは無効です（Retention:Enabled=false）。行は保持され続けます。");
            return;
        }

        logger.LogInformation(
            "order_dispatch_reservations（終端行のみ）の保持期間パージを開始します（保持 {Days} 日・間隔 {Interval}）。"
                + " Reserved（未確定）は対象外です。",
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
                // フェイルセーフ: パージの失敗で発注執行サービスを止めない（次回巡回で再試行する）。
                logger.LogError(ex, "order_dispatch_reservations のパージに失敗しました。次回巡回で再試行します。");
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

        // NFR-10, FR-11, #339: パージしてよいストアかを削除の前に確かめる。
        // 業務台帳・監査証跡（7 年保持）を指すよう配線が変わったら、1 行も消さずにここで落ちる。
        RetentionScope.EnsurePurgeable(PurgeTarget);

        var cutoff = RetentionPolicy.CutoffFor(clock.UtcNow, options.Value.RetentionDays);

        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOrderReservationStore>();

        var purged = store.PurgeCompletedBefore(cutoff, options.Value.EffectiveBatchSize);

        if (purged > 0)
            logger.LogInformation(
                "order_dispatch_reservations の終端行を {Count} 件パージしました（{Cutoff} より古い Completed）。",
                purged, cutoff);

        return Task.FromResult(purged);
    }
}
