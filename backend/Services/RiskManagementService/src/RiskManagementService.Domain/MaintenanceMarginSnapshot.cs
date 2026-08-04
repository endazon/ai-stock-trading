namespace AiStockTrading.RiskManagement.Domain;

// FR-10, UC-06, #330, IADR-0133 決定1: 維持率判定の入力（ブローカー由来の実測値の束）。
//
// **維持率 ＝ 純資産 ÷ 建玉評価額の合計**とする。ADR-0016 決定7 の規制側「実効維持率 ＝ 維持証拠金 ÷ 株価」と
// 分母が揃い、閾値（40% と規制要求の厳しい方）と同じ土俵で比較できる。
//
// 供給元（moomoo の口座照会）は未実装であり #342 / #331 の担当である。供給が無い間、本統制は 1 株も決済しない
// （IADR-0133 決定5。「動かす統制」の安全側は**動かない**ことである）。
public sealed record MaintenanceMarginSnapshot
{
    /// <summary>
    /// 純資産（USD・**日中の実測値**）。維持率の分子。
    /// <para>
    /// **統制上限の基準 equity（<c>PortfolioSnapshot.Capital</c>＝前営業日終値時点・当日中は不変。
    /// IADR-0130 決定2）とは別物である。** あちらは日中の含み損益で上限が動かないようにするための値、
    /// こちらは**日中に動くこと自体が判定対象**の値である。1 つにまとめると、日中に維持率が落ちても
    /// 判定が動かない（統制が沈黙する）。
    /// </para>
    /// </summary>
    public required decimal NetEquityUsd { get; init; }

    /// <summary>維持率の対象となる信用建玉（信用買い・空売り）。現物は含めない。</summary>
    public required IReadOnlyList<MarginPosition> Positions { get; init; }

    /// <summary>建玉評価額の合計（維持率の分母）。</summary>
    public decimal TotalMarketValueUsd => Positions.Sum(p => p.MarketValueUsd);

    /// <summary>
    /// 現在の維持率。建玉が無ければ **null**（維持率という概念が成立しない。ShortSellOrderContext と同じ扱い）。
    /// </summary>
    public decimal? MaintenanceMarginRatio =>
        TotalMarketValueUsd > 0m ? NetEquityUsd / TotalMarketValueUsd : null;
}
