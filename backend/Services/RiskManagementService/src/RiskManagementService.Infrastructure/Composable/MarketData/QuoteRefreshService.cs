using RiskManagementService.Application.Ports;
using RiskManagementService.Application.Services;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
// IADR-0128: Web SDK（旧 Worker）の暗黙 using に頼っていた型を、ライブラリ SDK では明示する。
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RiskManagementService.Infrastructure.MarketData;

// FR-10, #81, IADR-0066: 保有建玉の現在値を定期的に補充して QuoteCache へ入れる。判定の同期経路
// （OrderScreeningService → PortfolioSnapshotBuilder → IPortfolioStateProvider.GetCurrent）から市況取得の
// ネットワーク往復を切り離すため、取得はここ（背景）で行い、判定側は手元の値を読むだけにする。
//
// 既定では IMarketDataSource が no-op（常に取得不可）のため、本サービスは何も補充しない＝含み 0・DD 0 のまま。
// 台帳ストアは scoped（EF）のため巡回ごとに DI スコープを作る（MonitorPollingService と同じ規約）。
internal sealed class QuoteRefreshService(
    IServiceScopeFactory scopeFactory,
    IMarketDataSource marketData,
    QuoteCache cache,
    TimeProvider timeProvider,
    IOptions<MarketDataOptions> options,
    ILogger<QuoteRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.RefreshIntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break; // 停止要求
            }
            catch (Exception ex)
            {
                // フェイルセーフ: 補充の失敗で判定を止めない。取得できない現在値は前回値（保持期限内）か 0 に倒れる。
                logger.LogError(ex, "現在値の補充でエラーが発生しました。次回巡回を継続します。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    // 1 巡回。保有建玉ぶんの現在値を引き、取得できたものだけを手元へ保持する。単体テスト可能な単位として公開する。
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IPortfolioLedgerStore>();

        // 現在値が要るのは保有中の建玉だけ（IADR-0030 と同じ射影を再利用する）。
        var positions = PortfolioProjection.ProjectOpenPositions(ledger.GetFills());

        foreach (var position in positions)
        {
            var quote = await marketData
                .GetLatestQuoteAsync(position.Symbol, position.Market, cancellationToken)
                .ConfigureAwait(false);

            // 取得不可（null）は手元の前回値を残したまま次の銘柄へ（1 銘柄の失敗で全体を落とさない）。
            // 前回値が保持期限を超えれば読み出し側（CachedCurrentPriceSource）が取得不可として扱う。
            if (quote is not null)
                cache.Set(quote, timeProvider.GetUtcNow());
        }
    }
}
