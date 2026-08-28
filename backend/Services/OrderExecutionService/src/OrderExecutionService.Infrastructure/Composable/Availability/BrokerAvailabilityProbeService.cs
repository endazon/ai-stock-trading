using OrderExecutionService.Application.Availability;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Runtime;

namespace OrderExecutionService.Infrastructure.Availability;

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
//
// FR-19, #375, ADR-0021 決定3, IADR-0153: 同じ巡回で**口座種別**も観測して発行する（BrokerAccountObserved）。
// 新しい常駐を増やさないのは、口座種別の照会が接続時に既に行われている（TrdGetAccList）ためであり、
// 「稼働している ＝ 口座へ照会できる」という 1 つの事実から 2 つの観測が同時に得られるためである。
// 口座種別の供給元（IBrokerAccountSource）を実装しないアダプタ（内蔵 paper）では**発行しない**——
// 外部へ一度も発注しない擬似約定にはブローカー口座が存在しない。
internal sealed class BrokerAvailabilityProbeService(
    IBrokerAvailabilityProbe probe,
    IBrokerAdapter broker,
    IWolverineRuntime runtime,
    TimeProvider timeProvider,
    IOptions<BrokerAvailabilityProbeOptions> options,
    ILogger<BrokerAvailabilityProbeService> logger,
    IBrokerAccountSource? accountSource = null) : BackgroundService
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
        var bus = new MessageBus(runtime);
        var observedAt = timeProvider.GetUtcNow();
        await bus
            .PublishAsync(new BrokerAvailabilityObserved(
                broker.Provider, observedAt, options.Value.Interval))
            .ConfigureAwait(false);

        // FR-19, #375, ADR-0021 決定3: 口座種別の観測。**判明したときだけ発行する。**
        // 到達不能・照会失敗・種別不明はいずれも「発行しない」であり、受け手は沈黙を
        // 「口座種別を確認できていない」として新規建てを止める（フェイルクローズ）。
        await PublishAccountObservationAsync(bus, observedAt, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task PublishAccountObservationAsync(
        MessageBus bus, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        if (accountSource is null)
        {
            return;
        }

        var account = await accountSource.GetAccountStateAsync(cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            logger.LogWarning(
                "口座種別を確認できませんでした（照会失敗または種別不明）。今回は発行しません。"
                    + " 新規建ては口座種別が確認できるまで止まります（ADR-0021 決定3・FR-19）。");
            return;
        }

        await bus
            .PublishAsync(new BrokerAccountObserved(broker.Provider, account, observedAt))
            .ConfigureAwait(false);
    }
}
