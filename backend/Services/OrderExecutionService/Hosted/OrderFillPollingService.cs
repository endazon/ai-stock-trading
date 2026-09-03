using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.PollOrderFills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Runtime;

namespace OrderExecutionService.Hosted;

// #270, FR-05, FR-10, IADR-0113: 約定状態の追跡ポーリングを短周期（既定 30 秒）で回す。
//
// moomoo は発注時に Accepted（未約定）を返すため、約定の成立を観測しないと取引台帳（統制の入力）が
// 空のままになる。本サービスが非終端の発注結果を追跡し、終端化・部分約定の進捗を OrderExecuted として
// 再発行することで、SameDayReentry・日次発注上限・段階資金上限・建玉が moomoo 経路でも実効する。
//
// 配線は moomoo 選択時のみ（Program.cs）。paper は即時終端のため非終端記録が生まれず、仮に登録されても
// 照会は 1 度も起きない（二重の非干渉）。
//
// 滞留リコンサイル（IADR-0074/0092）とは対象集合が交わらない: あちらは Reserved（発注したか不明）な予約、
// こちらは確定済みの発注結果。両者が同一 OrderId の OrderExecuted を発行しても、台帳側の OrderId 単位の
// 単調 upsert により結果は冪等である。
public sealed class OrderFillPollingService(
    IServiceScopeFactory scopeFactory,
    IWolverineRuntime runtime,
    IOptions<FillPollingOptions> options,
    ILogger<OrderFillPollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            // 明示的に無効化された場合のみ止まる（既定は有効・IADR-0113）。統制が実効しなくなることを明示する。
            logger.LogWarning(
                "約定状態の追跡は無効です（FillPolling:Enabled=false）。"
                    + " 未約定のまま成立した約定は取引台帳へ届かず、統制上限は実効しません。");
            return;
        }

        logger.LogInformation(
            "約定状態の追跡を開始します（間隔 {Interval}・追跡上限 {MaxTracking}）。照会不達・不明は据え置きます（fail-safe）。",
            options.Value.Interval, options.Value.MaxTracking);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // フェイルセーフ: 追跡の失敗で発注執行サービスを止めない（次回巡回で再試行する）。
                logger.LogError(ex, "約定状態の追跡に失敗しました。次回巡回で再試行します。");
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

    // 1 巡回。終端化・進捗で得た OrderExecuted を発行する（単体テスト可能な単位として公開する）。
    public async Task<OrderFillPollResult> PollOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = scopeFactory.CreateScope();
        var poller = scope.ServiceProvider.GetRequiredService<OrderFillPoller>();

        var result = await poller
            .PollOnceAsync(options.Value.MaxTracking, options.Value.EffectiveBatchSize, cancellationToken)
            .ConfigureAwait(false);

        // ADR-0013, IADR-0129, #354: BackgroundService（singleton）からの発行。Wolverine の IMessageBus は scoped で
        // singleton へ注入できないため、singleton の IWolverineRuntime から MessageBus を作って発行する。
        var bus = new MessageBus(runtime);
        foreach (var executed in result.Executed)
            await bus.PublishAsync(executed).ConfigureAwait(false);

        if (result.Updated > 0 || result.Unknown > 0 || result.Failed > 0)
            logger.LogInformation(
                "約定追跡: 非終端 {Scanned} 件を照会（更新 {Updated} / 終端化 {Terminalized} / 変化なし {Unchanged}"
                    + " / 照会不能 {Unknown} / 失敗 {Failed}）。",
                result.Scanned, result.Updated, result.Terminalized, result.Unchanged, result.Unknown, result.Failed);

        return result;
    }
}
