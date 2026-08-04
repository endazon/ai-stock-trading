using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-10, UC-06, ADR-0016, #330, IADR-0133: 維持率割れによる自動縮小の対象となる信用建玉 1 件。
// 対象は**信用建玉（信用買い）と空売り建玉**であり（UC-06 代替フロー）、現物は維持率の概念を持たないため
// 供給側で除く。含み損・建玉時刻は**入力に持たない**——計画が対象選択の基準として明示的に棄却しており
// （「損切りの発想であり本統制の目的と別物」「維持率の回復と無関係」）、持てば必ず選択に混ざるためである。
public sealed record MarginPosition
{
    /// <summary>銘柄コード。日報の「決済した建玉」列に出る（04_report-templates）。</summary>
    public required string Symbol { get; init; }

    /// <summary>市場。空売りの対象は米国株のみ（ADR-0016 決定13）だが、信用買いは市場を限定しない。</summary>
    public required Market Market { get; init; }

    /// <summary>建玉の方向（Buy ＝ 信用買い／Sell ＝ 空売り）。決済は反対売買になる。</summary>
    public required TradeSide Side { get; init; }

    /// <summary>商品種別（<c>MarginLong</c> / <c>ShortSell</c>。IADR-0132 の 3 値）。日報の「方向」列に出る。</summary>
    public required ProductType ProductType { get; init; }

    /// <summary>建玉数量（株数）。端株は無いため、部分決済の単位も 1 株である。</summary>
    public required int Quantity { get; init; }

    /// <summary>現在値（USD）。維持率の分母（建玉評価額）と、適用閾値（株価依存）の両方に効く。</summary>
    public required decimal PriceUsd { get; init; }

    /// <summary>
    /// 必要証拠金（USD）。**ブローカー由来の実測値**であり実装が推定しない（IADR-0133 決定1）。
    /// 対象選択の唯一の基準であり（UC-06「必要証拠金の大きい建玉から順に決済する」）、
    /// 実装が規制式から再計算すると、ブローカーの実際の要求と食い違ったまま「回復したつもり」になり得る。
    /// </summary>
    public required decimal RequiredMarginUsd { get; init; }

    /// <summary>建玉評価額（USD）＝ 数量 × 現在値。維持率の分母の構成要素。</summary>
    public decimal MarketValueUsd => Quantity * PriceUsd;
}
