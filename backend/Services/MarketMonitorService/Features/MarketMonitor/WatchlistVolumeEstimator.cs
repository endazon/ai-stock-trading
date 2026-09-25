using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using MarketMonitorService.Features.MarketMonitor.ApplyWatchlistProposal;

namespace MarketMonitorService.Features.MarketMonitor;

// FR-01, ADR-0031 決定 2・3, ADR-0042 決定 1, #1025, IADR-0433 決定 4: 監視銘柄の数から Finnhub の 1 日の要求数を推定する。
//
// 🔴 **警告のみ（利用者裁定 2026-09-26）。** ADR-0042 決定 1 は Discord での適用に ADR-0031 の統制を求めるが、暫定上限 300 回/日
// （第三者観測・未実測）に対して現行の巡回（既定 60 秒）では 1 銘柄でも超えるため、拒否にすると追加が 1 件も通らない。
// 利用者は既存の見積り（IADR-0294）と同じく**警告に留め、追加を拒否しない**と裁定した（300 回/日の前提の見直しは計画側へ環流）。
// 推定の式は既存の見積りと同じ（銘柄数 × 1 巡回 1 要求 × 1 日の巡回回数）。数は申告値（EstimatedSymbolCount）ではなく
// **適用後の実際の監視銘柄の数**を使う。Finnhub を使わない構成（Provider が finnhub 以外）では null（対象外）。
public sealed class WatchlistVolumeEstimator(string? provider, int pollIntervalSeconds, int provisionalDailyLimit)
{
    public FinnhubDailyVolumeEstimateView? Estimate(int symbolCount)
    {
        if (!string.Equals(provider, "finnhub", StringComparison.OrdinalIgnoreCase))
            return null;

        var cycles = FinnhubDailyVolumeEstimator.CyclesPerDay(Math.Max(1, pollIntervalSeconds));
        var estimated = (long)Math.Max(0, symbolCount) * cycles;
        return new FinnhubDailyVolumeEstimateView(estimated, provisionalDailyLimit, estimated > provisionalDailyLimit);
    }
}
