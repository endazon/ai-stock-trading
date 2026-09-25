using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ObserveBrokerPositions;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Runtime;

namespace OrderExecutionService.Hosted;

// #292, FR-05, FR-10, IADR-0118: ブローカの現在建玉を定期照会し BrokerPositionsObserved として発行する。
//
// 突合そのものは行わない（台帳の権威はリスク管理にあり、こちらはブローカ接続だけを持つ）。
// 発注執行サービスは HTTP クライアント／s2s 配線を持たないため、逆方向（こちらがリスク管理を照会）にすると
// 認証サーフェスを新設することになる。観測を publish してリスク管理が突合する形を採る。
//
// fail-safe:
//   - 照会が null（不明）→ **何も発行しない**。空列（建玉ゼロ）とは意味が異なる。
//   - 例外 → 警告ログのみ。常駐は落とさず次回巡回で再試行する。
//
// 🔴 FR-10, UC-02, #880, IADR-0412 決定1: 同じスナップショットで**帰属不明の建玉の検知**も走らせる（相乗り）。
// 常駐ガードは Active な保護記録が 0 件の巡回では建玉を照会しないため、有効な記録が 1 件も無い口座では検知が走らなかった。
// こちらは保護記録の有無に依らず照会しているので、**照会（OpenD への往復）を 1 回も増やさずに**塞げる。
//   - unknown（null）→ 観測も検知もしない（「帰属不明なし」と読まない・通知済みの印も触らない）。
//   - none（空列）  → 観測を発行し、検知も走らせる（純額 0 の群の通知済みの印をリセットする）。
//   - present       → 観測を発行し、検知を走らせる。
//   - 検知の失敗は観測の発行を巻き戻さない（観測はリスク管理の突合の供給元）。エラーログを残して次回巡回で再試行する。
public sealed class BrokerPositionSnapshotService(
    IBrokerPositionSource positions,
    IWolverineRuntime runtime,
    TimeProvider timeProvider,
    IOptions<PositionReconciliationOptions> options,
    ILogger<BrokerPositionSnapshotService> logger,
    IServiceScopeFactory scopeFactory) : BackgroundService
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

        // ADR-0013, IADR-0129, #354: BackgroundService（singleton）からの発行。Wolverine の IMessageBus は scoped で
        // singleton へ注入できないため、singleton の IWolverineRuntime から MessageBus を作って発行する。
        var bus = new MessageBus(runtime);
        await bus
            .PublishAsync(new BrokerPositionsObserved(snapshot, timeProvider.GetUtcNow()))
            .ConfigureAwait(false);
        logger.LogDebug("ブローカ建玉 {Count} 件を観測として発行しました。", snapshot.Count);

        await DetectUnattributedAsync(snapshot, bus).ConfigureAwait(false);
        return true;
    }

    // 🔴 FR-10, #880, IADR-0412 決定1: 観測の発行の**後**に、同じスナップショットで帰属不明の建玉を検知して発行する。
    // 建玉照会はしない（上で取ったものを使う）。失敗は観測の発行を巻き戻さず、エラーログに留める。
    private async Task DetectUnattributedAsync(IReadOnlyList<BrokerPositionSnapshot> snapshot, MessageBus bus)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var detector = scope.ServiceProvider.GetRequiredService<UnattributedPositionDetector>();
            foreach (var evt in detector.Detect(snapshot))
                await bus.PublishAsync(evt).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "帰属不明の建玉の検知に失敗しました（建玉の観測は発行済み）。次回巡回で再試行します。");
        }
    }
}
