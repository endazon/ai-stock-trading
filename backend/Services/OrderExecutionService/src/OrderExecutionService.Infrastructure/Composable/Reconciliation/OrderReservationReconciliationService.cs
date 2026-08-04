using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.OrderExecution.Application.Reconciliation;
using AiStockTrading.OrderExecution.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Runtime;

namespace AiStockTrading.OrderExecution.Infrastructure.Composable.Reconciliation;

// #141, FR-05, IADR-0074: 滞留 Reserved（IADR-0057 の「発注済みか不明」の窓）の自動リコンサイルを定期実行する。
//
// 既定は無効（IADR-0074 決定4）。有効化は appsettings / Helm values の `Reconciliation:Enabled=true`。
// 有効化しても既定の no-op プローブ（IndeterminateReservationBrokerProbe）下では Placed/NotPlaced 経路は
// 発火せず、phase-4 自己修復（ブローカ非依存）のみが作動する。実 OpenD 照会プローブは opt-in の後続。
//
// 終端化した予約の OrderExecuted 発行は本 Worker 層が担う（Application はメッセージ基盤に非依存の既存レイヤリングを維持）。
internal sealed class OrderReservationReconciliationService(
    IServiceScopeFactory scopeFactory,
    IWolverineRuntime runtime,
    IClock clock,
    IOptions<ReconciliationOptions> options,
    ILogger<OrderReservationReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            // fail-safe: 明示的に有効化されるまで走査しない。
            logger.LogInformation(
                "発注予約の自動リコンサイルは無効です（Reconciliation:Enabled=false）。滞留 Reserved は人手/`_error` のままです。");
            return;
        }

        logger.LogInformation(
            "発注予約の自動リコンサイルを開始します（滞留閾値 {Hours} 時間・間隔 {Interval}）。"
                + " 照会不達・不確定は解放しません（fail-safe）。",
            ReconciliationPolicy.EffectiveStallThresholdHours(options.Value.StallThresholdHours),
            options.Value.Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // フェイルセーフ: リコンサイルの失敗で発注執行サービスを止めない（次回巡回で再試行する）。
                logger.LogError(ex, "発注予約の自動リコンサイルに失敗しました。次回巡回で再試行します。");
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

    // 1 巡回。終端化した予約の OrderExecuted を発行し、結果を返す（単体テスト可能な単位として公開する）。
    public async Task<ReservationReconciliationResult> ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cutoff = ReconciliationPolicy.StallCutoffFor(clock.UtcNow, options.Value.StallThresholdHours);

        using var scope = scopeFactory.CreateScope();
        var reconciler = scope.ServiceProvider.GetRequiredService<OrderReservationReconciler>();

        var result = await reconciler
            .ReconcileAsync(cutoff, options.Value.EffectiveBatchSize, cancellationToken).ConfigureAwait(false);

        // 終端化（発注済み確定・phase-4 自己修復）で得た OrderExecuted を発行し、下流（監査・Risk・通知）を追随させる。
        // ADR-0013, IADR-0129, #354: BackgroundService（singleton）からの発行。Wolverine の IMessageBus は scoped で
        // singleton へ注入できないため、singleton の IWolverineRuntime から MessageBus を作って発行する。
        foreach (var executed in result.Executed)
            await new MessageBus(runtime).PublishAsync(executed).ConfigureAwait(false);

        if (result.Scanned > 0)
            logger.LogInformation(
                "発注予約リコンサイル: 滞留 {Scanned} 件を走査（終端化 {Terminalized} / 解放 {Released} / 不確定 {Indeterminate} / 失敗 {Failed}）。",
                result.Scanned, result.Terminalized, result.Released, result.Indeterminate, result.Failed);

        if (result.Failed > 0)
            logger.LogWarning(
                "発注予約リコンサイルで {Failed} 件が例外により未処理でした（据え置き＝次回巡回で再試行）。", result.Failed);

        return result;
    }
}
