using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-10, UC-06, ADR-0016 決定7（2026-08-07 追記）, #420, IADR-0160:
// **維持率統制の単一情報源**（純関数）。算式・建玉 1 件の閾値・口座へ適用する閾値・回復目標を、
// ここだけで定義する。
//
// なぜ独立した型を置くのか: 適用閾値を **2 か所が別々に書いていた**ことが #420 の欠陥そのものである。
// 自動縮小（MaintenanceMarginReducer）は「保有建玉ごとの閾値の最大値」を採り、新規建ての評価
// （ShortSellEvaluator）は「これから出す注文の株価だけ」を見ていた。結果、**縮小が走っている最中に
// 評価器が積み増しを許す**（口座の実維持率 50%・$6.00 の空売り建玉あり＝要求 83.3% の状態で、
// $50.00 の新規空売りが閾値 40% と比較されて通る）。最大を採る場所が 1 か所しか無ければ、
// 片方だけ緩い状態は構造的に作れない。
//
// 計画（ADR-0016 決定7 の 2026-08-07 追記・利用者裁定 質問票 第 13 回 Q4）:
//   維持率の算式            = 純資産 ÷ 建玉評価額の合計（規制側の実効維持率と分母が揃う）
//   建玉が複数あるときの閾値 = 建玉ごとの閾値の**最大値**（FR-10「常に厳しい方が効く」の延長）
//   信用買いの規制維持率     = 時価の 25%（FINRA Rule 4210(c)(1)）
public static class MaintenanceMarginPolicy
{
    /// <summary>
    /// FR-10, ADR-0016 決定7（2026-08-07 追記）: **維持率 ＝ 純資産 ÷ 建玉評価額の合計。**
    /// 規制側の実効維持率（＝維持証拠金 ÷ 株価）と分母が揃うため、閾値と同じ土俵で比較できる。
    /// <para>
    /// **算式をここだけで定義する。** 維持率は外部（ブローカー）から供給される値でもあり得るが、
    /// 供給元が別定義の値（例: 買付余力基準）を返すと統制が黙ってずれる。計画が算式を確定した以上、
    /// 実装は「何を何で割った値か」を 1 か所に固定し、判定に用いる値はそこを通す。
    /// </para>
    /// </summary>
    /// <returns>建玉評価額の合計が 0 以下なら <c>null</c>（建玉が無ければ維持率という概念が成立しない）。</returns>
    public static decimal? Ratio(decimal netEquityUsd, decimal totalMarketValueUsd) =>
        totalMarketValueUsd > 0m ? netEquityUsd / totalMarketValueUsd : null;

    /// <summary>
    /// FR-10, ADR-0016 決定7: 建玉 1 件に要求される維持率 ＝「自前の 40%」と「規制上の要求」の**厳しい方**。
    /// 規制側は**商品種別で条文が異なる**（<see cref="RegulatoryFor"/>）。
    /// </summary>
    /// <param name="limits">空売り専用統制値（自前閾値 40% の保持元）。</param>
    /// <param name="priceUsd">建玉の株価（USD）。</param>
    /// <param name="productType">商品種別。空売り＝4210(c)(3) ／ 信用買い＝4210(c)(1)。</param>
    public static decimal ThresholdFor(ShortSellingLimits limits, decimal priceUsd, ProductType productType)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return Math.Max(limits.MaintenanceMarginThreshold, RegulatoryFor(priceUsd, productType));
    }

    /// <summary>
    /// FR-10, ADR-0016 決定7（2026-08-07 追記）: 規制上の実効維持率を**商品種別ごとの条文**で解く。
    /// <list type="bullet">
    /// <item>空売り（<see cref="ProductType.ShortSell"/>）＝ **4210(c)(3)** <c>max($5.00 ÷ 株価, 30%)</c></item>
    /// <item>信用買い（<see cref="ProductType.MarginLong"/>）＝ **4210(c)(1)** 時価の **25%**</item>
    /// </list>
    /// <para>
    /// **現物（<see cref="ProductType.Cash"/>）は維持率の対象ではない**（供給側が除く。
    /// <see cref="MaintenanceMarginSnapshot.Positions"/> の定義）。万一渡された場合は空売り側の式へ倒す
    /// ——既知の中で最も厳しい要求であり、**緩む側へ倒さない**。
    /// </para>
    /// </summary>
    public static decimal RegulatoryFor(decimal priceUsd, ProductType productType) => productType switch
    {
        ProductType.MarginLong => ShortSellingLimits.RegulatoryMarginLongMaintenanceMargin,
        _ => ShortSellingLimits.RegulatoryMaintenanceMarginFor(priceUsd),
    };

    /// <summary>
    /// FR-10, ADR-0016 決定7（2026-08-07 追記）, #420: **口座へ適用する維持率閾値 ＝ 建玉ごとの閾値の最大値。**
    /// 候補は「保有している信用建玉」と、**これから建てる建玉**（新規注文自身も建玉になるため候補に含める）。
    /// <para>
    /// 候補が 1 つも無い（建玉なし・新規建てなし）場合は維持率という概念が成立しないため**例外**とする。
    /// 呼び出し側が先に短絡すること（<see cref="MaintenanceMarginReducer.Plan"/> は建玉ゼロで <c>null</c> を返す）。
    /// 既定値へ倒すと「建玉が無いのに閾値がある」という成立しない状態を作る。
    /// </para>
    /// </summary>
    /// <param name="limits">空売り専用統制値。</param>
    /// <param name="positions">保有している信用建玉（信用買い・空売り）。</param>
    /// <param name="newEntryPriceUsd">これから建てる建玉の株価。新規建てを伴わない判定では <c>null</c>。</param>
    /// <param name="newEntryProductType">これから建てる建玉の商品種別。</param>
    public static decimal AppliedThreshold(
        ShortSellingLimits limits,
        IEnumerable<MarginPosition> positions,
        decimal? newEntryPriceUsd = null,
        ProductType newEntryProductType = ProductType.ShortSell)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(positions);

        var candidates = positions.Select(p => ThresholdFor(limits, p.PriceUsd, p.ProductType));
        if (newEntryPriceUsd is { } price)
        {
            candidates = candidates.Append(ThresholdFor(limits, price, newEntryProductType));
        }

        return candidates.Max();
    }

    /// <summary>
    /// FR-10, UC-06, §5: 維持率割れによる自動縮小の回復目標 ＝ **適用閾値 + 5 ポイント**。
    /// 閾値が規制側で上がれば回復目標も同じだけ上がる（**連動する**）。オフセットは定数であるため、
    /// 「建玉ごとの回復目標の最大値」と「適用閾値 + オフセット」は常に一致する。
    /// </summary>
    public static decimal AppliedRecoveryTarget(
        ShortSellingLimits limits,
        IEnumerable<MarginPosition> positions,
        decimal? newEntryPriceUsd = null,
        ProductType newEntryProductType = ProductType.ShortSell)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return AppliedThreshold(limits, positions, newEntryPriceUsd, newEntryProductType)
            + limits.MaintenanceRecoveryTargetOffset;
    }
}
