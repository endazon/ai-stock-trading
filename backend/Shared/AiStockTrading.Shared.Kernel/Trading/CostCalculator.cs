using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Kernel.Trading;

// FR-17, 05_trading-assumptions §4: 費用関数（純関数）。数値計算はコードで行い LLM には計算させない（05 採用方針）。
//
// 🔴 FR-06, FR-16, 計画 ADR-0035 決定 4・5, #1201, IADR-0501: **事前見積りと事後集計を別の関数にする。**
//   - 事前見積り（判断時の採算判定・バックテスト）: EstimateOneWayCost＝手数料 + 為替スプレッド相当。
//     §4 の「為替スプレッド相当」は円建てで調達した資金の実効コストを採算へ織り込むためのもので、**事前見積りに限る**。
//   - 事後集計（報告書の費用合計・実現損益の控除項）: FillCost＝手数料 + 取引諸費用（米国株の売却時の SEC・TAF）。
//     **為替スプレッドを約定ごとに乗せない**——外貨決済では約定ごとの両替が発生せず、両替は入出金時にだけ起きる（決定 4）。
public static class CostCalculator
{
    // 片道の概算費用 = 市場別手数料 ＋ 為替スプレッド（**非基準通貨市場**に約定代金比で適用）。
    // #364, IADR-0152 決定7: 為替スプレッドは通貨の交換に伴う費用であり、基準通貨の市場では交換が発生しない。
    // 旧実装は市場（Market.Japan）を直書きしており、「基準通貨は JPY」という前提に暗黙に依存していた。
    // MarketCurrency.IsBaseCurrency へ一般化し、基準通貨が変わっても定義に忠実であり続けるようにする
    //（結果として基準通貨 USD では日本市場へ適用が反転する）。
    public static decimal EstimateOneWayCost(TradingAssumptions assumptions, Market market, decimal notional) =>
        EstimateOneWayCostBreakdown(assumptions, market, notional).Total;

    // FR-17, #615, IADR-0305: 片道の概算費用（**事前見積り**）を区分ごとに返す。
    //
    // 🔴 **式は 1 か所にしか無い。** EstimateOneWayCost は本関数の Total を返す——内訳版を別式で書くと、
    // 内訳の合計が費用合計と一致しなくなる（しかも両方とも「それらしい数字」なので気付けない）。
    //
    // 🔴 FR-06, 計画 ADR-0035 決定 4, #1201, IADR-0501: 本型の <c>FxSpread</c> は**判断時の事前見積り**であり、
    // 報告書の事後集計には使わない（事後集計は <see cref="FillCost"/>。為替スプレッドの実績は入出金時の両替にだけ掛かる）。
    /// <summary>
    /// 片道の概算費用の内訳（事前見積り）。<b><see cref="Total"/> は <see cref="EstimateOneWayCost"/> と同値である</b>。
    /// </summary>
    public readonly record struct OneWayCostBreakdown(decimal Commission, decimal FxSpread)
    {
        /// <summary>費用合計（手数料＋為替スプレッド相当額）。</summary>
        public decimal Total => Commission + FxSpread;
    }

    /// <summary>片道の概算費用を区分ごとに見積もる（純関数）。</summary>
    public static OneWayCostBreakdown EstimateOneWayCostBreakdown(
        TradingAssumptions assumptions, Market market, decimal notional)
    {
        ArgumentNullException.ThrowIfNull(assumptions);

        var commission = Commission(assumptions, market, notional);
        var fxSpread = MarketCurrency.IsBaseCurrency(market) ? 0m : notional * assumptions.FxSpreadRatio;
        return new OneWayCostBreakdown(commission, fxSpread);
    }

    // FR-06, FR-16, FR-17, 計画 ADR-0035 決定 3・4・5, 04_report-templates §数値の定義, #1201, IADR-0501:
    // **事後集計**（報告書）の約定 1 件の費用。実現損益の定義「約定代金差額 −（売買手数料＋取引諸費用）− 源泉徴収税額」の
    // 控除項そのものである。
    //
    // 🔴 **為替スプレッドを持たない**（決定 4。約定ごとの両替は発生しない）。事前見積りの為替スプレッドと同じ値にしない。
    // 🔴 **借株料も持たない**——単位が確定するまで費用計算の入口へ接続しない（ADR-0016 決定3。構造テストが固定する）。
    //    報告書は借株料を記録（借株料の計上）から別に受け取り、費用合計へ足す。
    /// <summary>
    /// 事後集計の約定 1 件の費用内訳（売買手数料＋取引諸費用）。
    /// </summary>
    /// <param name="Commission">売買手数料（事前見積りと同じ式）。</param>
    /// <param name="RegulatoryFees">取引諸費用（米国株の売り約定の SEC Section 31 手数料＋FINRA 取引活動料。それ以外は 0）。</param>
    public readonly record struct FillCostBreakdown(decimal Commission, decimal RegulatoryFees)
    {
        /// <summary>売買手数料＋取引諸費用。</summary>
        public decimal Total => Commission + RegulatoryFees;
    }

    /// <summary>
    /// 約定 1 件の事後集計の費用（純関数）。取引諸費用は<b>米国市場の売り約定（空売りを含む）だけ</b>に掛かる
    /// （05_trading-assumptions §2「いずれも売却時のみ発生する」）。料率は前提条件の設定点
    /// <see cref="TradingAssumptions.UnitedStatesSellRegulatoryFees"/> から読む。
    /// </summary>
    public static FillCostBreakdown FillCost(
        TradingAssumptions assumptions, Market market, TradeSide side, int quantity, decimal price)
    {
        ArgumentNullException.ThrowIfNull(assumptions);

        var notional = quantity * price;
        var regulatory = market == Market.UnitedStates && side == TradeSide.Sell
            ? assumptions.UnitedStatesSellRegulatoryFees.For(notional, quantity)
            : 0m;
        return new FillCostBreakdown(Commission(assumptions, market, notional), regulatory);
    }

    // 売買手数料の式は 1 か所（事前見積りと事後集計で同じ値になる）。
    private static decimal Commission(TradingAssumptions assumptions, Market market, decimal notional) =>
        (market == Market.Japan ? assumptions.JapanCommission : assumptions.UnitedStatesCommission).For(notional);

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
    /// <summary>
    /// 最小期待利益のしきい値。<b>解が無いときは <c>null</c> を返す（＝算出不能・当該取引は見送り）。</b>
    /// <para>
    /// <b><c>null</c> は「未設定」ではなく「見送り」を意味する。</b> 分母 <c>1 − 倍率 × 税率</c> が 0 以下になると、
    /// どれだけ利益が大きくても条件を満たせない（利益を増やすと税も同じ速さで増える）。
    /// 呼び出し側は<b>採算不能とみなして見送る</b>こと——<b>既定値で埋めたり 0 とみなしたりしてはならない</b>
    /// （どちらも「全通過」と同じ結果になる）。
    /// </para>
    /// <para>
    /// FR-17, 05_trading-assumptions §4「解が無い領域では見送る」（利用者裁定 2026-08-08・
    /// planning#289）, #461, IADR-0177: <b>3 経路（共有契約・採算ゲート・本関数）が
    /// 同じ意味を返す。</b> 本関数は 2026-08-08 まで <c>InvalidOperationException</c> を送出しており、
    /// 通過させない向きは合っていたが<b>「見送り」とは壊れ方が違った</b>（処理ごと落ちる）。
    /// </para>
    /// </summary>
    public static decimal? MinimumViableProfit(TradingAssumptions assumptions, Market market, decimal notional)
    {
        ArgumentNullException.ThrowIfNull(assumptions);

        var roundTrip = EstimateRoundTripCost(assumptions, market, notional);

        // 式の単一情報源は Shared.Contracts の MinimumExpectedProfit（#358・IADR-0173 決定3）。
        // 採算ゲート（TradeDecisionService.Domain）も同じ関数を使う——別ユニットの Domain どうしは互いを
        // 参照できないため、式を 2 か所に書くと片方だけ直したときに気付けない。
        //
        // #461, IADR-0177: 解無し（倍率 × 税率 >= 1）は共有契約と同じく null で返す。番兵値は採らない
        //——数値として扱えてしまうことが「負のしきい値で全通過」の再発経路になる。
        return MinimumExpectedProfit.Threshold(
            roundTrip, assumptions.MinimumExpectedProfitMultiple, assumptions.CapitalGainsTaxRate);
    }
}
