using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision;

// FR-04, FR-15, #1118, IADR-0467 決定 2・3: 日足の取得元（発注執行の GET /order-execution/daily-bars）。1 呼び出し＝1 回の照会。
// キャッシュ・当日足の除外・取引日の判定は持たない（CachedDailyBarsProvider の責務）。
// 🔴 **null＝取得できない**（非 2xx・例外・契約の食い違い・送り手の Unavailable）。キャンセルは伝える。
public interface IDailyBarsSource
{
    Task<IReadOnlyList<DailyBar>?> FetchAsync(
        string symbol, Market market, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}
