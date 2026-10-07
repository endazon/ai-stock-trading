using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Domain;

// FR-03, FR-13, SC-02, ADR-0043（計画）決定 2 (b)・4, #1030, IADR-0437: 「1 巡回が巡回間隔に収まること」の判定（純関数）。
//
// 市場監視は 1 巡回で保有銘柄と監視銘柄の現在値を取る（`MarketMonitorAppService`）。
// #1189, IADR-0494: 同じ（銘柄・市場）は 1 巡回に 1 回だけ照会し両方に使うため、保有と監視銘柄の**和集合**で数える
// （`CycleQuoteTargets`。巡回と同じ関数・同じ鍵）。以前は「保有 ＋ 監視銘柄」の足し算で、重なる銘柄を 2 回数えていた。
// 送出は自制レート（回/分）のトークンバケットで抑えられるため、1 巡回の要求数 ÷ 自制レート（回/分）が巡回間隔（分）を
// 超えると、巡回が間隔に収まらず 1 銘柄あたりの価格の確認が遅れる（＝損切りの判定が遅れる）。
//
//   収まる ⇔ 1 巡回の Finnhub の要求数 × 60 ≤ 自制レート × 巡回間隔（秒）
//   1 巡回の要求数 ＝ Σ（保有 ∪ 監視銘柄）の 1 銘柄あたりの要求数（ADR-0043 決定 2 (b)「1 巡回で問い合わせる銘柄数 × 1 銘柄あたりの要求数」）
//
// 🔴 **1 銘柄あたりの要求数は市場で決まる**: 米国 1・それ以外 0。Finnhub 無料枠の /quote は米国株だけで、
// `FinnhubMarketDataSource` は米国以外の銘柄を**要求を出さずに**取得不可とする（#1037 の監査）。東証の銘柄を数えると、
// 予算を使わない追加を誤って拒否する。
// 整数のまま比べる（分へ割ると丸めで境界がずれる）。自制レート・巡回間隔の 0 以下は実装と同じく 1 へ寄せる
// （`MarketDataSourceFactory.Limiter` と `MonitorPollingService` の `Math.Max(1, …)`）。
//
// 🔴 残余リスク（IADR-0494）: 保有は判定の時点の照会で数える。後から監視銘柄の外の銘柄を保有すると、巡回はその分だけ多く照会する
// （送出はトークンバケットが待たせ、巡回が間隔を超えて損切りの判定が遅れる。要求は落とさない）。
public sealed record WatchlistCycleFit(int RequestsPerMinute, int PollIntervalSeconds, IReadOnlyCollection<MonitoredSymbol> Holdings)
{
    private long Rate => Math.Max(1, RequestsPerMinute);

    private long Interval => Math.Max(1, PollIntervalSeconds);

    /// <summary>その市場の銘柄 1 件が 1 巡回で使う Finnhub の要求数（米国 1・それ以外 0）。</summary>
    public static int RequestsPerSymbol(Market market) => market == Market.UnitedStates ? 1 : 0;

    /// <summary>1 巡回に収まる要求数（自制レート × 巡回間隔（分）の切り捨て）。</summary>
    public long Capacity => Rate * Interval / 60;

    /// <summary>監視銘柄が <paramref name="watchlist"/> のときの 1 巡回の要求数（保有と監視銘柄の和集合。重なりは 1 回）。</summary>
    public long RequestsPerCycle(IEnumerable<MonitoredSymbol> watchlist) =>
        CycleQuoteTargets.Union(Holdings, watchlist).Sum(s => (long)RequestsPerSymbol(s.Market));

    /// <summary>監視銘柄が <paramref name="watchlist"/> のとき、1 巡回が巡回間隔に収まるか。</summary>
    public bool Fits(IEnumerable<MonitoredSymbol> watchlist) => RequestsPerCycle(watchlist) * 60 <= Rate * Interval;

    /// <summary>
    /// <paramref name="added"/> を足して <paramref name="after"/> になる追加を拒否するか。要求を使わない追加は、
    /// 予算を超えていても拒否しない（予算を増やさないため）。
    /// </summary>
    /// <remarks>
    /// #1189, IADR-0494 決定 2: 「要求を使わない」は<b>限界の要求数</b>で判定する（足す前と後の 1 巡回の要求数を比べる）。
    /// 米国以外の銘柄に加え、<b>既に保有している銘柄</b>も、巡回は保有のループで既に照会しているので要求を増やさない
    /// （保有だけで予算を超えていても、保有中の銘柄を監視銘柄に足すことは止めない）。
    /// </remarks>
    public bool Refuses(MonitoredSymbol added, IEnumerable<MonitoredSymbol> after)
    {
        ArgumentNullException.ThrowIfNull(added);
        var afterList = after as IReadOnlyCollection<MonitoredSymbol> ?? [.. after];
        var before = afterList.Where(s => !Equals(s, added));
        return AddsRequests(added, before) && !Fits(afterList);
    }

    /// <summary>
    /// #1189, IADR-0494 決定 2: 監視銘柄 <paramref name="watchlist"/> に <paramref name="added"/> を足すと 1 巡回の要求数が増えるか
    /// （米国の銘柄で、保有にも <paramref name="watchlist"/> にも無いとき＝和集合が増えるとき）。
    /// </summary>
    public bool AddsRequests(MonitoredSymbol added, IEnumerable<MonitoredSymbol> watchlist)
    {
        ArgumentNullException.ThrowIfNull(added);
        var without = watchlist as IReadOnlyCollection<MonitoredSymbol> ?? [.. watchlist];
        return RequestsPerCycle([.. without, added]) > RequestsPerCycle(without);
    }

    /// <summary>収まらないときの理由（SC-02 の 400・入れ替え案の内訳に載せる短い文）。</summary>
    /// <remarks>
    /// #1189, IADR-0494: 内訳は要求を使う（米国の）銘柄の数で、保有 a ＋ 監視銘柄 b − 重複 c ＝ 1 巡回の要求数（b は監視銘柄の
    /// 重複を除いた数）。重複が 0 でも「− 重複 0」を出す（式の形を毎回見せる）。
    /// </remarks>
    public string Describe(IReadOnlyCollection<MonitoredSymbol> watchlist)
    {
        var held = CostlyDistinct(Holdings);
        var watched = CostlyDistinct(watchlist);
        var overlap = held.Intersect(watched).Count();
        return $"1 巡回 {RequestsPerCycle(watchlist)} 要求（保有 {held.Count} ＋ 監視銘柄 {watched.Count} − 重複 {overlap}）が、"
            + $"自制 {Rate} 回/分・巡回間隔 {Interval} 秒で収まる {Capacity} 要求を超えます";
    }

    private static HashSet<MonitoredSymbol> CostlyDistinct(IEnumerable<MonitoredSymbol> symbols) =>
        [.. symbols.Where(s => RequestsPerSymbol(s.Market) > 0)];
}
