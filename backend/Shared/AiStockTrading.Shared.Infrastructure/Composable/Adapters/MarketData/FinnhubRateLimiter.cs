using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;

namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;

// FR-01, FR-03, ADR-0043 決定2 (a), #1247, IADR-0513: Finnhub へ送る限流器の唯一の生成点（市況 4 サービスと情報収集の Finnhub 系）。
//
// Finnhub の鍵は 60 回 / 60 秒の固定窓（IADR-0275）で、プロセス間の協調はしない（IADR-0068 決定4）。
// 容量 C・自制 r 回/分のトークンバケットは、長さ 60 秒の任意の窓 [t, t+60) で最大 C + r − 1 回を送る。
// 容量 ＝ r（是正前）では 2r − 1 回になり、同じ鍵の 5 プロセスが同じ窓へ重なると Σ(2r − 1) ＝ 109 回（> 60）を送り得た。
// 容量を 1 にする（60/r 秒に 1 回の等間隔）と、どの窓でも r 回以下になり、構成の合計 Σr ≤ 60（IADR-0512 の検査）が
// そのまま「どの固定窓でも合計 ≤ 60」になる。窓の位置（Finnhub 側は最初の要求の時刻から 60 秒）には依らない。
//
// 共有の TokenBucket は変えない（Finnhub 以外の提供元はバーストを許したまま。IADR-0513 決定2）。
public static class FinnhubRateLimiter
{
    /// <summary>
    /// 容量 1・補充間隔 ⌈1 分 ÷ r⌉（ティック単位で切り上げ）のバケット。0 以下の r は 1 回/分へ寄せる（fail-safe）。
    /// </summary>
    /// <remarks>
    /// 間隔を切り捨てると r 回の間隔の合計が 60 秒を割り、r + 1 回目が同じ窓へ入る。切り上げで必ず 60 秒以上にする。
    /// </remarks>
    public static TokenBucket CreateBucket(int requestsPerMinute)
    {
        var rate = Math.Max(1, requestsPerMinute);
        var intervalTicks = (TimeSpan.TicksPerMinute + rate - 1) / rate;
        return new TokenBucket(1, TimeSpan.FromTicks(intervalTicks));
    }

    /// <summary>送出前に待つ限流器（<see cref="CreateBucket"/> を <see cref="DelayingRateLimiter"/> で包む）。</summary>
    public static IRateLimiter Create(int requestsPerMinute, TimeProvider timeProvider) =>
        new DelayingRateLimiter(CreateBucket(requestsPerMinute), timeProvider);
}
