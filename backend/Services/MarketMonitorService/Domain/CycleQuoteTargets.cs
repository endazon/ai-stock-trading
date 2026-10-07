namespace MarketMonitorService.Domain;

// FR-03, FR-01, ADR-0043（計画）決定 2 (b)・3, #1189, IADR-0494: 1 巡回で現在値を照会する（銘柄・市場）の集合。
//
// 市場監視は 1 巡回の中で同じ（銘柄・市場）を 1 回だけ照会し、保有の損切り評価と監視銘柄の急変検知の両方に使う
// （`MarketMonitorAppService`）。そこで 1 巡回の要求数（`WatchlistCycleFit`）と日次の見積りの母数
// （`MonitorRoundResult.QuotedSymbolMarkets`・入れ替え案の応答）も、保有と監視銘柄の**和集合**で数える。
// 巡回・予算・見積りの 3 か所が同じ関数を使う（数え方を食い違わせない）。
//
// 🔴 **鍵は序数比較の（銘柄コード, 市場）**（`MonitoredSymbol` の record の等価）。大小文字・前後空白は正規化しない。
// 照会は銘柄をそのまま提供元へ送る（`FinnhubQuoteClient` は正規化しない）ため、`aapl` と `AAPL` は別の要求である。
// 「同じ引数で同じソースを呼ぶ」ときだけ畳む（共有の `QuoteCache` の鍵と同じ）。市場が違えば別の銘柄。
public static class CycleQuoteTargets
{
    /// <summary>保有ポジションの照会の鍵。</summary>
    public static MonitoredSymbol Of(HeldPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        return new MonitoredSymbol(position.Symbol, position.Market);
    }

    /// <summary>
    /// 保有と監視銘柄の和集合（重なりは 1 件。最初に現れた順＝保有が先）。1 巡回で照会する（銘柄・市場）そのもの。
    /// </summary>
    public static IReadOnlyList<MonitoredSymbol> Union(IEnumerable<MonitoredSymbol> holdings, IEnumerable<MonitoredSymbol> watchlist)
    {
        ArgumentNullException.ThrowIfNull(holdings);
        ArgumentNullException.ThrowIfNull(watchlist);
        return [.. holdings.Concat(watchlist).Distinct()];
    }
}
