using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.Shared.Kernel.Trading;
using MarketMonitorService.Domain;
using MarketMonitorService.Features.MarketMonitor.ApplyWatchlistProposal;

namespace MarketMonitorService.Features.MarketMonitor;

// FR-01, ADR-0031 決定 2・3, ADR-0042 決定 1, #1025, IADR-0433 決定 4: 監視銘柄の数から Finnhub の 1 日の要求数を推定する（警告のみ）。
//
// 🔴 ADR-0043（計画）決定 1・3, #1030, IADR-0437:
// - **1 日の巡回回数は開場中の巡回だけで数える。** 市場監視は全市場が閉じている間は巡回せず、閉場中の市場の銘柄も照会しない
//   （`MarketMonitorAppService`）。そこで銘柄ごとに、その市場の場中の長さ ÷ 巡回間隔で数える（米国 390 分）。東証の銘柄は Finnhub の要求を使わないので 0（#1037 の監査）。
// - **保有銘柄も数える。** 巡回は保有と監視銘柄を別々に照会する（同じ銘柄でも 2 要求）。
// - **暫定の 300 回/日とは比べない**（撤回）。日次上限を実測して設定したとき（`Finnhub:ProvisionalDailyLimit`）だけ比べる。
// Finnhub を使わない構成（Provider が finnhub 以外）では null（対象外）。適用を止めない（止めるのは巡回が間隔に収まるかの検査）。
public sealed class WatchlistVolumeEstimator(string? provider, int pollIntervalSeconds, int? dailyLimit)
{
    /// <param name="symbolMarkets">1 巡回で照会する銘柄の市場（適用後の監視銘柄 ＋ 保有）。</param>
    public FinnhubDailyVolumeEstimateView? Estimate(IEnumerable<Market> symbolMarkets)
    {
        ArgumentNullException.ThrowIfNull(symbolMarkets);
        if (!string.Equals(provider, "finnhub", StringComparison.OrdinalIgnoreCase))
            return null;

        // #1037 の監査: 1 銘柄あたりの要求数は市場で決まる（米国 1・それ以外 0。FinnhubMarketDataSource は米国以外で要求を出さない）。
        var interval = Math.Max(1, pollIntervalSeconds);
        var estimated = symbolMarkets.Sum(m =>
            (long)WatchlistCycleFit.RequestsPerSymbol(m)
            * FinnhubDailyVolumeEstimator.CyclesPerDay(interval, MarketSessions.RegularSessionMinutes(m)));
        return new FinnhubDailyVolumeEstimateView(estimated, dailyLimit, dailyLimit is { } limit && estimated > limit);
    }
}
