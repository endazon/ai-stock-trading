using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, FR-01, #1131, IADR-0473: 現在値の「いつから数えて新しいか」と「いま引きに行くか」の規則（純関数）。
// 補充（QuoteRefreshService）と読み出し（CachedCurrentPriceSource）が**同じ規則**を見るため 1 か所に置く。
//
// 🔴 **開場判定の実体は共有カーネルの MarketHours である**（市場監視・取引判断と同じ答え。新しい判定を作らない）。
//
// 閉場中は価格が動かない。したがって閉場中に引いた値は「次の開場の瞬間に引いた値」と同じだけ新しい —— これが
// 「閉場中は引かない」と「閉場中も価格を読める」を両立させる根拠である（窓の両端。IADR-0473 決定 2）。
//   - 補充を止めるだけ（取得時刻から数える）だと、引けの 5 分後から翌朝まで手元の値がすべて期限切れになり、
//     閉場中の手仕舞いの参照価格・実DD のサンプリング・審査の含み損益が価格を失う。
//   - 起点を常に次の開場にすると、場中に引いた値まで翌朝まで信じることになる（場中の鮮度が壊れる）。
public static class QuoteSessionFreshness
{
    /// <summary>
    /// 鮮度を数え始める時刻。取得時に開場していれば取得時刻、閉場していればその閉場の次の開場時刻。
    /// 次の開場を見通せなければ（未知の市場・長期休場）取得時刻へ倒す（従来どおりの鮮度＝期限切れの側）。
    /// </summary>
    public static DateTimeOffset FreshFrom(Market market, DateTimeOffset fetchedAt)
    {
        if (MarketHours.IsOpen(market, fetchedAt))
            return fetchedAt;

        return MarketHours.NextOpen(market, fetchedAt) is { } nextOpen && nextOpen > fetchedAt
            ? nextOpen
            : fetchedAt;
    }

    /// <summary>手元の値がまだ読めるか（鮮度の起点から <paramref name="maxStaleness"/> 以内）。</summary>
    public static bool IsFresh(Market market, DateTimeOffset fetchedAt, DateTimeOffset now, TimeSpan maxStaleness) =>
        now - FreshFrom(market, fetchedAt) <= maxStaleness;

    /// <summary>
    /// いま引きに行くか。開場中は毎巡回引く。閉場中は、手元に「この閉場の中で引いた値」が無いときだけ引く
    /// （引けの後の 1 回・閉場中の再起動・前回の取得が取得不可だった場合）。
    /// </summary>
    /// <param name="market">建玉の市場（市場ごとに判定する）。</param>
    /// <param name="now">いま。</param>
    /// <param name="fetchedAt">手元の値の取得時刻。無ければ null。</param>
    public static bool ShouldRefresh(Market market, DateTimeOffset now, DateTimeOffset? fetchedAt) =>
        MarketHours.IsOpen(market, now)
        || fetchedAt is not { } at
        || FreshFrom(market, at) <= now;
}
