using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Domain;

// FR-03, FR-13, SC-02, ADR-0043（計画）決定 2 (b)・4, #1030, IADR-0437: 「1 巡回が巡回間隔に収まること」の判定（純関数）。
//
// 市場監視は 1 巡回で保有銘柄と監視銘柄の現在値を 1 件ずつ取る（`MarketMonitorAppService`。同じ銘柄でも別々に照会する）。
// 送出は自制レート（回/分）のトークンバケットで抑えられるため、1 巡回の要求数 ÷ 自制レート（回/分）が巡回間隔（分）を
// 超えると、巡回が間隔に収まらず 1 銘柄あたりの価格の確認が遅れる（＝損切りの判定が遅れる）。
//
//   収まる ⇔ 1 巡回の Finnhub の要求数 × 60 ≤ 自制レート × 巡回間隔（秒）
//   1 巡回の要求数 ＝ Σ（保有 ＋ 監視銘柄）の 1 銘柄あたりの要求数（ADR-0043 決定 2 (b)「銘柄数 × 1 銘柄あたりの要求数」）
//
// 🔴 **1 銘柄あたりの要求数は市場で決まる**: 米国 1・それ以外 0。Finnhub 無料枠の /quote は米国株だけで、
// `FinnhubMarketDataSource` は米国以外の銘柄を**要求を出さずに**取得不可とする（#1037 の監査）。東証の銘柄を数えると、
// 予算を使わない追加を誤って拒否する。
// 整数のまま比べる（分へ割ると丸めで境界がずれる）。自制レート・巡回間隔の 0 以下は実装と同じく 1 へ寄せる
// （`MarketDataSourceFactory.Limiter` と `MonitorPollingService` の `Math.Max(1, …)`）。
public sealed record WatchlistCycleFit(int RequestsPerMinute, int PollIntervalSeconds, int HoldingRequests)
{
    private long Rate => Math.Max(1, RequestsPerMinute);

    private long Interval => Math.Max(1, PollIntervalSeconds);

    /// <summary>その市場の銘柄 1 件が 1 巡回で使う Finnhub の要求数（米国 1・それ以外 0）。</summary>
    public static int RequestsPerSymbol(Market market) => market == Market.UnitedStates ? 1 : 0;

    /// <summary>1 巡回に収まる要求数（自制レート × 巡回間隔（分）の切り捨て）。</summary>
    public long Capacity => Rate * Interval / 60;

    /// <summary>監視銘柄が <paramref name="watchlist"/> のときの 1 巡回の要求数（保有 ＋ 監視銘柄）。</summary>
    public long RequestsPerCycle(IEnumerable<MonitoredSymbol> watchlist) =>
        (long)Math.Max(0, HoldingRequests) + watchlist.Sum(s => RequestsPerSymbol(s.Market));

    /// <summary>監視銘柄が <paramref name="watchlist"/> のとき、1 巡回が巡回間隔に収まるか。</summary>
    public bool Fits(IEnumerable<MonitoredSymbol> watchlist) => RequestsPerCycle(watchlist) * 60 <= Rate * Interval;

    /// <summary>
    /// <paramref name="added"/> を足して <paramref name="after"/> になる追加を拒否するか。要求を使わない追加（米国以外）は、
    /// 予算を超えていても拒否しない（予算を増やさないため）。
    /// </summary>
    public bool Refuses(MonitoredSymbol added, IEnumerable<MonitoredSymbol> after) =>
        RequestsPerSymbol(added.Market) > 0 && !Fits(after);

    /// <summary>収まらないときの理由（SC-02 の 400・入れ替え案の内訳に載せる短い文）。</summary>
    public string Describe(IReadOnlyCollection<MonitoredSymbol> watchlist) =>
        $"1 巡回 {RequestsPerCycle(watchlist)} 要求（保有 {Math.Max(0, HoldingRequests)} ＋ 監視銘柄 {watchlist.Sum(s => RequestsPerSymbol(s.Market))}）が、"
        + $"自制 {Rate} 回/分・巡回間隔 {Interval} 秒で収まる {Capacity} 要求を超えます";
}
