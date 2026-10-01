using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.Shared.Kernel.Trading;
// IADR-0128: Web SDK（旧 Worker）の暗黙 using に頼っていた型を、ライブラリ SDK では明示する。
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RiskManagementService.Hosted;

// FR-10, #81, IADR-0066: 保有建玉の現在値を定期的に補充して QuoteCache へ入れる。判定の同期経路
// （OrderScreeningService → PortfolioSnapshotBuilder → IPortfolioStateProvider.GetCurrent）から市況取得の
// ネットワーク往復を切り離すため、取得はここ（背景）で行い、判定側は手元の値を読むだけにする。
//
// 既定では IMarketDataSource が no-op（常に取得不可）のため、本サービスは何も補充しない＝含み 0・DD 0 のまま。
// 台帳ストアは scoped（EF）のため巡回ごとに DI スコープを作る（MonitorPollingService と同じ規約）。
//
// FR-01, FR-10, #1131, IADR-0473: **閉場中は引かない**（市場ごと。開場判定は市場監視と同じ共有カーネルの MarketHours）。
// 是正前は開場に関係なく巡回し、引け後の 3 時間で Finnhub を約 510 回呼んでいた（市場監視は 0 回）。
// ただし閉場ごとに 1 回だけは引く（引けの後・閉場中の再起動）。その値は次の開場から鮮度を数えるため
// （QuoteSessionFreshness）、閉場中に読む側（手仕舞いの参照価格・実DD・審査の含み損益）は価格を失わない。
// 開場後は最初の巡回で引く（閉場中の値は開場から保持期限まで有効なので、巡回間隔が保持期限未満なら途切れない）。
public sealed class QuoteRefreshService(
    IServiceScopeFactory scopeFactory,
    IMarketDataSource marketData,
    QuoteCache cache,
    TimeProvider timeProvider,
    IOptions<MarketDataOptions> options,
    ILogger<QuoteRefreshService> logger,
    FinnhubDailyVolumeRecorder? dailyVolume = null) : BackgroundService
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

        // FR-01, ADR-0031（計画）決定2〜4, ADR-0043（計画）決定 3, #1132, IADR-0477: 日次要求見積りを巡回ごとに保有建玉の実数から
        // 記録する（是正前は起動時に運用者の申告 1 銘柄で数えていた）。閉場中の巡回でも同じ値（開場中の量を数える。
        // #1131, IADR-0473 決定 3: 1 日の巡回は米国の場中 390 分で数え、閉場ごとの 1 回は数えない）。
        dailyVolume?.Record(positions.Select(p => p.Market), options.Value.RefreshIntervalSeconds);

        var now = timeProvider.GetUtcNow();
        foreach (var position in positions)
        {
            // #1131, IADR-0473: 閉場中で、この閉場の中で引いた値が手元にあれば引かない（閉場中は価格が動かない）。
            if (!QuoteSessionFreshness.ShouldRefresh(
                    position.Market, now, cache.GetEntry(position.Symbol, position.Market)?.FetchedAt))
                continue;

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
