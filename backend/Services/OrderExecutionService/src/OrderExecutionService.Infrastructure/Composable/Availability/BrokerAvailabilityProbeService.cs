using AiStockTrading.OrderExecution.Application.Availability;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Runtime;

namespace AiStockTrading.OrderExecution.Infrastructure.Composable.Availability;

// FR-20, FR-05, #385, 06_daytrading-review §4.2, IADR-0150 決定1: ブローカ（OpenD）へ到達できるかを定期に確かめ、
// 到達できた事実だけを BrokerAvailabilityObserved として発行する。Stage 1 の「稼働分数」の供給元である。
//
// **なぜ接続・切断のイベントではなく定期 probe なのか**（IADR-0150 決定1）:
// 切断イベントは、それ自体が最初に失われる。OpenD の異常終了・プロセス断・ネットワーク断では
// 「開始」だけが記録されて「終了」が届かず、区間が閉じないまま稼働時間が伸び続ける（＝営業日数の水増し）。
// 定期 probe は**沈黙が「稼働していない」を意味する**ため、供給が途絶えた側が安全に倒れる。
//
// fail-safe:
//   - probe が false（到達不能）→ **何も発行しない**（受け手はその区間を稼働として積まない）
//   - 例外 → 警告ログのみ。常駐は落とさず次回巡回で再試行する（発行はしない）
internal sealed class BrokerAvailabilityProbeService(
    IBrokerAvailabilityProbe probe,
    IBrokerAdapter broker,
    IWolverineRuntime runtime,
    TimeProvider timeProvider,
    IOptions<BrokerAvailabilityProbeOptions> options,
    ILogger<BrokerAvailabilityProbeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            // 明示的に無効化された場合のみ止まる（既定は有効）。Stage 1 が進まなくなることを明示する。
            logger.LogWarning(
                "ブローカ稼働の観測は無効です（{Section}:Enabled=false）。"
                    + " Stage 1 の営業日数は 1 日も積まれず、昇格の期間条件は永久に満たされません（FR-20・§4.2）。",
                BrokerAvailabilityProbeOptions.SectionName);
            return;
        }

        logger.LogInformation(
            "ブローカ稼働の定期観測を開始します（発注先 {Provider}・間隔 {Interval}）。到達できない巡回は発行しません（fail-safe）。",
            broker.Provider, options.Value.Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProbeOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ブローカ稼働の観測に失敗しました。次回巡回で再試行します。");
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

    /// <summary>1 巡回。発行したら true（到達不能で発行しなかった場合は false）。単体テスト可能な単位。</summary>
    public async Task<bool> ProbeOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var operational = await probe.IsOperationalAsync(cancellationToken).ConfigureAwait(false);
        if (!operational)
        {
            // 「到達できなかった」を発行しない。受け手は沈黙をそのまま稼働 0 分として扱う（§4.2 の除外）。
            logger.LogWarning("ブローカ（OpenD）へ到達できませんでした。今回は稼働として発行しません。");
            return false;
        }

        // ADR-0013, IADR-0129, #354: BackgroundService（singleton）からの発行。Wolverine の IMessageBus は scoped で
        // singleton へ注入できないため、singleton の IWolverineRuntime から MessageBus を作って発行する。
        //
        // 発注先は**実際に接続しているアダプタの自己申告**（IADR-0149 決定1 と同じ出どころ）。段階が定める
        // 既定の発注先（Stage.Mode）ではない——内蔵 paper で稼働していても SIMULATE として計上されてしまう。
        await new MessageBus(runtime)
            .PublishAsync(new BrokerAvailabilityObserved(
                broker.Provider, timeProvider.GetUtcNow(), options.Value.Interval))
            .ConfigureAwait(false);
        return true;
    }
}
