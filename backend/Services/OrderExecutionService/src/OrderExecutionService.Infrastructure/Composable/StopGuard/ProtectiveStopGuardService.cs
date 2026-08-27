using AiStockTrading.OrderExecution.Application.StopGuard;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Runtime;

namespace AiStockTrading.OrderExecution.Infrastructure.Composable.StopGuard;

// FR-10, UC-02, #331, IADR-0210 決定4: 保護逆指値の失効検知・再発注・残存取消を定期巡回で回す。
// **逆指値なしの建玉を持たない**（FR-10）の滞留側の守り手であり、エントリー時の同時発注
// （OrderExecutionService）と対で 1 つの不変条件を成す。
//
// 配線は moomoo 選択時のみ（Program.cs）——判定の前提（ブローカー注文照会・建玉照会
// 〔IBrokerPositionSource〕）を paper が持たないため。paper の逆指値は滞留 Accepted であり、
// 失効・建玉消滅の分岐は単体テスト（フェイク注入）で固定する。
internal sealed class ProtectiveStopGuardService(
    IServiceScopeFactory scopeFactory,
    IWolverineRuntime runtime,
    IOptions<ProtectiveStopGuardOptions> options,
    ILogger<ProtectiveStopGuardService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            // 明示的に無効化された場合のみ止まる（既定は有効）。失効した逆指値が検知されなくなることを明示する。
            logger.LogWarning(
                "保護逆指値ガードは無効です（{Section}:Enabled=false）。"
                    + " 逆指値の失効・建玉消滅後の残存注文は検知されず、逆指値なしの建玉が残り得ます（FR-10）。",
                ProtectiveStopGuardOptions.SectionName);
            return;
        }

        logger.LogInformation(
            "保護逆指値ガードを開始します（間隔 {Interval}・バッチ {BatchSize}）。照会不能は据え置きます（fail-safe）。",
            options.Value.Interval, options.Value.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
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
                // フェイルセーフ: ガードの失敗で発注執行サービスを止めない（次回巡回で再試行する）。
                logger.LogError(ex, "保護逆指値ガードの巡回に失敗しました。次回巡回で再試行します。");
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

    // 1 巡回。再発注・保護喪失で得たイベントを発行する（単体テスト可能な単位として公開する）。
    public async Task<ProtectiveStopGuardResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = scopeFactory.CreateScope();
        var guard = scope.ServiceProvider.GetRequiredService<ProtectiveStopGuard>();

        var result = await guard.RunOnceAsync(options.Value.BatchSize, cancellationToken).ConfigureAwait(false);

        // ADR-0013, IADR-0129, #354: BackgroundService（singleton）からの発行。Wolverine の IMessageBus は scoped で
        // singleton へ注入できないため、singleton の IWolverineRuntime から MessageBus を作って発行する。
        var bus = new MessageBus(runtime);
        foreach (var evt in result.Events)
            await bus.PublishAsync(evt).ConfigureAwait(false);

        if (result.Replaced > 0 || result.ClosedOut > 0 || result.Unknown > 0 || result.Failed > 0)
            logger.LogWarning(
                "保護逆指値ガード: Active {Scanned} 件を評価（維持 {StillActive} / 完了 {Completed} / 再発注 {Replaced}"
                    + " / 手仕舞い {ClosedOut} / 照会不能 {Unknown} / 失敗 {Failed}）。",
                result.Scanned, result.StillActive, result.Completed, result.Replaced,
                result.ClosedOut, result.Unknown, result.Failed);

        return result;
    }
}
