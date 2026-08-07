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
    /// 現在の維持率。建玉が無ければ **null**（維持率という概念が成立しない）。
    /// <para>
    /// #420, IADR-0160: 算式（純資産 ÷ 建玉評価額の合計。ADR-0016 決定7 の 2026-08-07 追記）は
    /// <see cref="MaintenanceMarginPolicy.Ratio"/> だけが定義する。本型は割り算を再実装しない。
    /// </para>
    /// </summary>
    public decimal? MaintenanceMarginRatio => MaintenanceMarginPolicy.Ratio(NetEquityUsd, TotalMarketValueUsd);

    /// <summary>
    /// 値として成立しない建玉（IADR-0133 決定8）。**株価・数量が 0 以下**、または**必要証拠金が負**のもの。
    /// <para>
    /// いずれも市場・口座の実態としてあり得ず、**フィードが壊れていることの証拠**である。株価と数量は
    /// 維持率の分母（建玉評価額）を、必要証拠金は決済の優先順位を直接歪めるため、混ざったまま計算すると
    /// 「実際より良い維持率」「誤った順序」を生む。
    /// </para>
    /// </summary>
    public IReadOnlyList<MarginPosition> UntrustedPositions =>
        [.. Positions.Where(p => p.PriceUsd <= 0m || p.Quantity <= 0 || p.RequiredMarginUsd < 0m)];

    /// <summary>
    /// スナップショット全体を信頼してよいか。**1 件でも壊れていれば全体を信頼しない**
    /// （壊れた建玉だけを除くと分母が縮んで維持率が実際より良く見え、過少縮小へ倒れる。IADR-0133 決定8）。
    /// </summary>
    public bool IsTrustworthy => UntrustedPositions.Count == 0;
}
