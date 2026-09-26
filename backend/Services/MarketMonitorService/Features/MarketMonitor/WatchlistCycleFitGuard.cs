using AiStockTrading.Shared.Contracts.Trading;
using MarketMonitorService.Domain;

namespace MarketMonitorService.Features.MarketMonitor;

// FR-03, FR-13, SC-02, ADR-0043（計画）決定 2 (b)・4, #1030, IADR-0437: 監視銘柄を増やす変更の前に「1 巡回が巡回間隔に収まること」の
// 判定材料（自制レート・巡回間隔・保有）を揃える。SC-02 の追加・全置換・Discord の入れ替え案の適用の 3 つの口が同じものを使う
// （経路ごとに統制を食い違わせない。ADR-0043 決定 4）。
//
// - Finnhub を使わない構成（MarketData:Provider が finnhub 以外）では自制レートが無いため null（検査しない）。
// - 自制レートは現在値ソースと同じ構成（MarketData:Finnhub:RequestsPerMinute）、巡回間隔は巡回と同じ構成（Monitor:PollIntervalSeconds）を
//   Program.cs が渡す（値の出所を巡回・送出と 1 つにする）。
// - 保有は巡回と同じ `IPositionStore` から、Finnhub の要求を使う（米国の）建玉だけを数える（#1037 の監査）。照会の失敗は空列（0 件）になるが、そのとき巡回も保有を照会しないため、
//   その瞬間の 1 巡回の要求数とは一致する（残余リスクは IADR-0437）。
public sealed class WatchlistCycleFitGuard(
    string? provider,
    int requestsPerMinute,
    int pollIntervalSeconds,
    IPositionStore positions)
{
    public bool Applies => string.Equals(provider?.Trim(), "finnhub", StringComparison.OrdinalIgnoreCase);

    /// <summary>判定材料。Finnhub を使わない構成では null（検査しない）。</summary>
    public async Task<WatchlistCycleFitSnapshot?> ResolveAsync(CancellationToken cancellationToken = default)
    {
        if (!Applies)
            return null;

        var held = await positions.GetOpenPositionsAsync(cancellationToken).ConfigureAwait(false);
        return new WatchlistCycleFitSnapshot(
            new WatchlistCycleFit(requestsPerMinute, pollIntervalSeconds, held.Sum(p => WatchlistCycleFit.RequestsPerSymbol(p.Market))),
            [.. held.Select(p => p.Market)]);
    }
}

// 判定（Fit）と、1 日の要求数の見積りに使う保有の市場（HoldingMarkets。市場ごとの場中の長さで数えるため）。
public sealed record WatchlistCycleFitSnapshot(WatchlistCycleFit Fit, IReadOnlyList<Market> HoldingMarkets);
