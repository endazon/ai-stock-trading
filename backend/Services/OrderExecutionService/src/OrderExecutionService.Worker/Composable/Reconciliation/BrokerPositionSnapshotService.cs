using AiStockTrading.OrderExecution.Application.Reconciliation;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using MassTransit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiStockTrading.OrderExecution.Worker.Composable.Reconciliation;

// #292, FR-05, FR-10, IADR-0118: ブローカの現在建玉を定期照会し BrokerPositionsObserved として発行する。
//
// 突合そのものは行わない（台帳の権威はリスク管理にあり、こちらはブローカ接続だけを持つ）。
// 発注執行サービスは HTTP クライアント／s2s 配線を持たないため、逆方向（こちらがリスク管理を照会）にすると
// 認証サーフェスを新設することになる。観測を publish してリスク管理が突合する形を採る。
//
// fail-safe:
//   - 照会が null（不明）→ **何も発行しない**。空列（建玉ゼロ）とは意味が異なる。
//   - 例外 → 警告ログのみ。常駐は落とさず次回巡回で再試行する。
internal sealed class BrokerPositionSnapshotService(
    IBrokerPositionSource positions,
    IBus bus,
    TimeProvider timeProvider,
    IOptions<PositionReconciliationOptions> options,
    ILogger<BrokerPositionSnapshotService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            // 明示的に無効化された場合のみ止まる（既定は有効）。乖離が見えなくなることを明示する。
            logger.LogWarning(
                "ブローカ建玉の突合は無効です（Reconciliation:Positions:Enabled=false）。"
                    + " 手動売買・外部約定による台帳との乖離は検知されません。");
            return;
        }

        logger.LogInformation(
            "ブローカ建玉の定期照会を開始します（間隔 {Interval}）。照会不能は発行せず据え置きます（fail-safe）。",
            options.Value.Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ブローカ建玉の照会に失敗しました。次回巡回で再試行します。");
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

    /// <summary>1 巡回。発行したら true（照会不能で発行しなかった場合は false）。単体テスト可能な単位。</summary>
    public async Task<bool> PublishOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = await positions.GetPositionsAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            // 「不明」を「建玉ゼロ」と取り違えない。発行すれば台帳の全建玉が乖離として報告される。
            logger.LogWarning("ブローカ建玉を照会できませんでした（不明）。今回は発行しません。");
            return false;
        }

        await bus.Publish(new BrokerPositionsObserved(snapshot, timeProvider.GetUtcNow()), cancellationToken)
            .ConfigureAwait(false);
        logger.LogDebug("ブローカ建玉 {Count} 件を観測として発行しました。", snapshot.Count);
        return true;
    }
}
