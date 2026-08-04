namespace AiStockTrading.Shared.Contracts.Trading;

/// <summary>
/// FR-19, ADR-0007, ADR-0016 決定1, #332, IADR-0132: 取引可能な商品種別。
/// <para>
/// **3 値をそれぞれ独立に有効・無効へ設定でき、既定はいずれも「現物のみ有効」**である
/// （FR-19 本文・05_trading-assumptions §5 表「取引可能な商品種別」）。2 値（現物 / 信用）では
/// 段階解禁（ADR-0016 決定8: Stage 2 は現物のみ・信用買いと空売りは Stage 3）を設定として表現できない。
/// </para>
/// <para>
/// **序数は永続化・画面と結びついている**（設定 JSON は数値 enum・画面も数値で送受信する。IADR-0086 決定4）。
/// 旧 <c>Margin = 1</c> の位置を <see cref="MarginLong"/> が引き継いでおり、序数を変えると既存の
/// 「有効な商品種別」が別の意味に化ける。**値の並べ替え・挿入を行わないこと。**
/// </para>
/// </summary>
public enum ProductType
{
    /// <summary>現物（自己資金で買う）。損失の上限あり（投資額まで）。</summary>
    Cash = 0,

    /// <summary>信用買い（資金を借りて買う）。損失の上限あり（投資額まで）。ADR-0007 が想定した回転売買。</summary>
    MarginLong = 1,

    /// <summary>
    /// 空売り（株を借りて売る）。**損失に上限が無い**ため専用統制を上乗せする（ADR-0016 決定2〜9・
    /// <c>ShortSellEvaluator</c>）。対象市場は米国株のみ（決定13）。
    /// </summary>
    ShortSell = 2,
}
