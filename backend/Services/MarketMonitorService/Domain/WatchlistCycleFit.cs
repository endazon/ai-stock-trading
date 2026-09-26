namespace MarketMonitorService.Domain;

// FR-03, FR-13, SC-02, ADR-0043（計画）決定 2 (b)・4, #1030, IADR-0437: 「1 巡回が巡回間隔に収まること」の判定（純関数）。
//
// 市場監視は 1 巡回で保有銘柄と監視銘柄の現在値を 1 件ずつ取る（`MarketMonitorAppService`。同じ銘柄でも別々に照会する）。
// 送出は自制レート（回/分）のトークンバケットで抑えられるため、1 巡回の要求数 ÷ 自制レート（回/分）が巡回間隔（分）を
// 超えると、巡回が間隔に収まらず 1 銘柄あたりの価格の確認が遅れる（＝損切りの判定が遅れる）。
//
//   収まる ⇔ (保有数 ＋ 監視銘柄数) × 60 ≤ 自制レート × 巡回間隔（秒）
//
// 整数のまま比べる（分へ割ると丸めで境界がずれる）。自制レート・巡回間隔の 0 以下は実装と同じく 1 へ寄せる
// （`MarketDataSourceFactory.Limiter` と `MonitorPollingService` の `Math.Max(1, …)`）。
// 数え方は保守側: 市場を問わず全銘柄を数える（米国と東証の場中は重ならないが、判定を市場ごとに割らない）。
public sealed record WatchlistCycleFit(int RequestsPerMinute, int PollIntervalSeconds, int HoldingsCount)
{
    private long Rate => Math.Max(1, RequestsPerMinute);

    private long Interval => Math.Max(1, PollIntervalSeconds);

    /// <summary>1 巡回に収まる要求数（自制レート × 巡回間隔（分）の切り捨て）。</summary>
    public long Capacity => Rate * Interval / 60;

    /// <summary>監視銘柄が <paramref name="watchlistCount"/> 件のときの 1 巡回の要求数（保有 ＋ 監視銘柄）。</summary>
    public long RequestsPerCycle(int watchlistCount) => (long)Math.Max(0, HoldingsCount) + Math.Max(0, watchlistCount);

    /// <summary>監視銘柄が <paramref name="watchlistCount"/> 件のとき、1 巡回が巡回間隔に収まるか。</summary>
    public bool Fits(int watchlistCount) => RequestsPerCycle(watchlistCount) * 60 <= Rate * Interval;

    /// <summary>収まらないときの理由（SC-02 の 400・入れ替え案の内訳に載せる短い文）。</summary>
    public string Describe(int watchlistCount) =>
        $"1 巡回 {RequestsPerCycle(watchlistCount)} 要求（保有 {Math.Max(0, HoldingsCount)} ＋ 監視銘柄 {watchlistCount}）が、"
        + $"自制 {Rate} 回/分・巡回間隔 {Interval} 秒で収まる {Capacity} 要求を超えます";
}
