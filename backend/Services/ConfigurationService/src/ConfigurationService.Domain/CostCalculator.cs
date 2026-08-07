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

    // 最小期待利益（この額を下回る期待利益の取引は見送り）。
    // FR-17, §4, #358, IADR-0173: 基準は **往復費用＋税** であり、往復費用のみではない
    //（利用者決定 2026-07-23。2026-07-18 の実装は計画確定前の暫定値のままだった）。
    //
    // **税は結果に依存するため、しきい値は不動点として解く。** 譲渡益税は譲渡益（= 利益 − 費用）に掛かるため、
    //   T = m × (C + (T − C) × r)      m = 倍率 / C = 費用 / r = 譲渡益税率
    // を T について解いて
    //   T = m × C × (1 − r) / (1 − m × r)
    // となる。m = 2 / r = 0.20315 では **T ≈ 2.684 × C**（旧実装の 1.5 × C の約 1.79 倍）。
    //
    // 本式は「税引後で費用の m 倍が残る」ことを意味しない。計画が書いているのは
    // 「利益が**費用と税の合計**の m 倍以上であること」であり、本式はそれをそのまま解いたものである。
    public static decimal MinimumViableProfit(TradingAssumptions assumptions, Market market, decimal notional)
    {
        ArgumentNullException.ThrowIfNull(assumptions);

        var roundTrip = EstimateRoundTripCost(assumptions, market, notional);

        // 式の単一情報源は Shared.Contracts の MinimumExpectedProfit（#358・IADR-0173 決定3）。
        // 採算ゲート（TradeDecision.Domain）も同じ関数を使う——別ユニットの Domain どうしは互いを
        // 参照できないため、式を 2 か所に書くと片方だけ直したときに気付けない。
        return MinimumExpectedProfit.Threshold(
            roundTrip, assumptions.MinimumExpectedProfitMultiple, assumptions.CapitalGainsTaxRate)
            ?? throw new InvalidOperationException(
                "最小期待利益のしきい値に解が無い（倍率 × 税率 >= 1）。"
                    + $" 倍率={assumptions.MinimumExpectedProfitMultiple} 税率={assumptions.CapitalGainsTaxRate}");
    }
}
