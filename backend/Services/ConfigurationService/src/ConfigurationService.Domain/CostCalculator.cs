using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Configuration.Domain;

// FR-17, 05_trading-assumptions §4: 概算費用関数（純関数）。判断時の事前見積り・リスク判定・事後集計で共通利用する。
// 費用(市場, 約定代金) = 手数料 + 為替スプレッド相当。数値計算はコードで行い LLM には計算させない（05 採用方針）。
public static class CostCalculator
{
    // 片道の概算費用 = 市場別手数料 ＋ 為替スプレッド（**非基準通貨市場**に約定代金比で適用）。
    // #364, IADR-0152 決定7: 為替スプレッドは通貨の交換に伴う費用であり、基準通貨の市場では交換が発生しない。
    // 旧実装は市場（Market.Japan）を直書きしており、「基準通貨は JPY」という前提に暗黙に依存していた。
    // MarketCurrency.IsBaseCurrency へ一般化し、基準通貨が変わっても定義に忠実であり続けるようにする
    //（結果として基準通貨 USD では日本市場へ適用が反転する）。
    public static decimal EstimateOneWayCost(TradingAssumptions assumptions, Market market, decimal notional)
    {
        ArgumentNullException.ThrowIfNull(assumptions);

        var schedule = market == Market.Japan ? assumptions.JapanCommission : assumptions.UnitedStatesCommission;
        var commission = schedule.For(notional);
        var fxSpread = MarketCurrency.IsBaseCurrency(market) ? 0m : notional * assumptions.FxSpreadRatio;
        return commission + fxSpread;
    }

    // 往復（建て＋手仕舞い）の概算費用。
    public static decimal EstimateRoundTripCost(TradingAssumptions assumptions, Market market, decimal notional) =>
        2m * EstimateOneWayCost(assumptions, market, notional);

    // 最小期待利益（この額を下回る期待利益の取引は見送り）。= 往復費用 × 最小期待利益倍率。
    // 税の精緻化（往復費用＋税ベース）は実損益連携時の後続（IADR-0021）。
    public static decimal MinimumViableProfit(TradingAssumptions assumptions, Market market, decimal notional)
    {
        ArgumentNullException.ThrowIfNull(assumptions);
        return EstimateRoundTripCost(assumptions, market, notional) * assumptions.MinimumExpectedProfitMultiple;
    }
}
