using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;
using AppSvc = AiStockTrading.InformationCollection.Application.Services.InformationCollectionService;

namespace AiStockTrading.InformationCollection.Worker.Composable.Polling;

// FR-01, FR-02, UC-01: 収集間隔ごとのポーリング。1 巡回で収集→正規化→サニタイズ→KB 保存し、収集があれば InformationCollected
// を発行して取引サイクル（FR-02）の起点にする。巡回の例外は握りつぶしてログする（フェイルセーフ・収集を止めない）。
// NFR（費用）, IADR-0031: 各巡回で費用統制（#23）を照会し、Halted なら巡回をスキップ（サイクル停止）、Throttled なら間隔を延長する。
internal sealed class CollectionPollingService(
    IServiceScopeFactory scopeFactory,
    IOptions<CollectionOptions> options,
    ILogger<CollectionPollingService> logger) : BackgroundService
{
    // Halted 時の再照会倍率（収集はしないが、復帰検知のため Throttled と同じ間隔で回す）。
    private const decimal HaltedRecheckMultiplier = 2m;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var baseInterval = TimeSpan.FromSeconds(Math.Max(1, options.Value.PollIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            var gate = CostControlGate.Normal;
            try
            {
                gate = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "情報収集の巡回でエラーが発生しました。次回巡回を継続します。");
            }

            try
            {
                await Task.Delay(EffectiveInterval(baseInterval, gate), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // 費用統制の状態から次回巡回までの実効間隔を算出する純関数（境界テスト用）。
    // Normal=base、Throttled=base×（サーバ応答の IntervalMultiplier をそのまま使用・下限1・現状 CostGovernor は 2×）、
    // Halted=base×2（収集はしないが復帰検知のため再照会）。
    public static TimeSpan EffectiveInterval(TimeSpan baseInterval, CostControlGate gate)
    {
        var multiplier = gate.Halted ? HaltedRecheckMultiplier : Math.Max(1m, gate.IntervalMultiplier);
        return baseInterval * (double)multiplier;
    }

    // 1 巡回。費用統制を照会し、Halted なら収集/発行をスキップする。適用する統制ゲートを返す（間隔算出に用いる）。
    // 単体テスト可能な単位として公開する。
    public async Task<CostControlGate> RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var gate = await scope.ServiceProvider.GetRequiredService<ICostControlGate>()
            .GetAsync(cancellationToken).ConfigureAwait(false);

        if (gate.Halted)
        {
            // NFR（費用）: LLM 月次上限 100% 到達＝停止。収集も発行もしない（サイクルを回さない）。
            logger.LogWarning("費用統制が停止（Halted）のため今回の収集巡回をスキップします。");
            return gate;
        }

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

        return gate;
    }
}
