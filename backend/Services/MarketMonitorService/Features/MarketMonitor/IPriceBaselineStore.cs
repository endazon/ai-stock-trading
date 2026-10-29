using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Features.MarketMonitor;

// FR-03: 変動判定の基準値（前回 AI 判断時点の価格）を銘柄別に保持する。
// 基準値の更新契機は TradeDecisionMade（発注意図あり）と、UC-02, #1077, IADR-0452 以降は TradeDecisionHeld
// （AI 判断後の見送り。Hold を含む）の購読である。未設定銘柄は null を返す。
public interface IPriceBaselineStore
{
    decimal? GetBaseline(string symbol, Market market);

    void SetBaseline(string symbol, Market market, decimal price);

    /// <summary>
    /// 🔴 FR-03, #1487, IADR-0596: 基準値と、それを置いた時刻（分からなければ <c>null</c>）。取引日をまたいだ基準値を置き直すために読む。
    /// 既定の実装（時刻を持たないストア）は時刻を null で返す（置き直さない＝従来どおり）。
    /// </summary>
    PriceBaseline? GetBaselineEntry(string symbol, Market market) =>
        GetBaseline(symbol, market) is { } price ? new PriceBaseline(price, null) : null;
}

/// <summary>#1487, IADR-0596: 基準値（前回判断時点の価格）と、置いた時刻（UTC。分からなければ null）。</summary>
public sealed record PriceBaseline(decimal Price, DateTimeOffset? SetAt);
