using AiStockTrading.Shared.Contracts.Trading;

namespace ReportService.Domain;

// FR-06, FR-16, #1181, IADR-0493 決定 1・3: **期間開始時点の在庫**（報告書の窓の市場ごとの下端より前に閉場したセッションまでの
// 台帳行を、リスク管理の取引台帳が畳んだもの）。報告書の在庫の畳み込み（PnlAggregator・FillPnlAttributionBuilder・
// TradeHistoryViewBuilder・ReportDraftService の現在値の決定・FxTranslationBuilder）の**初期値**である。
//
// 🔴 **取得原価をここで畳み直さない。** 平均取得単価の権威は台帳の畳み込み（リスク管理の PortfolioProjection.ApplyToLot・
// IADR-0033）であり、報告書は返された値をそのまま初期在庫に置く（2 か所で畳むと規則がずれ得る。IADR-0493 §却下した案 b）。
//
// 🔴 **null（受け取っていない）と空（期間開始時点で建玉なし）を取り違えない。** null の畳み込みは従来どおり期間で切った在庫
// （IADR-0381）であり、期間より前に建てた建玉の決済は「算定できない」と数える。
public sealed record OpeningInventorySnapshot(IReadOnlyList<OpeningLot> Lots)
{
    /// <summary>期間開始時点で建玉が無い（照会は成功した）。</summary>
    public static OpeningInventorySnapshot Empty { get; } = new([]);

    /// <summary>
    /// 畳み込みの初期在庫（(銘柄, 市場) → 符号付き在庫・基準通貨の平均取得単価）。<paramref name="opening"/> が null なら空
    /// ＝従来どおり期間で切った在庫から始める。同じ (銘柄, 市場) が複数あれば後勝ち（供給元は市場ごとに 1 行しか返さない）。
    /// </summary>
    public static Dictionary<(string Symbol, Market Market), InventoryLot> Seed(OpeningInventorySnapshot? opening)
    {
        var lots = new Dictionary<(string Symbol, Market Market), InventoryLot>();
        if (opening is null)
            return lots;

        foreach (var lot in opening.Lots)
            lots[(lot.Symbol, lot.Market)] = new InventoryLot(lot.SignedQuantity, lot.AverageCost);

        return lots;
    }
}

/// <summary>
/// FR-06, FR-16, #1181, IADR-0493 決定 2: 期間開始時点の在庫 1 銘柄ぶん。
/// </summary>
/// <param name="SignedQuantity">符号付き数量（ロング +・ショート −。0 は持たない）。</param>
/// <param name="AverageCost">基準通貨（USD）建ての平均取得単価（約定の <see cref="PeriodTradeFill.Price"/> と同じ通貨）。</param>
/// <param name="AverageFxRateBaseToDisplay">
/// 認識時レート（1 USD あたりの円）の加重平均。<b><c>null</c>＝建玉に未記録の約定が含まれ平均を作れない</b>（為替差損益は未供給）。
/// </param>
/// <param name="UnrecordedFxRateFillCount">建玉に残る、認識時レートが未記録の台帳行の数。</param>
public sealed record OpeningLot(
    string Symbol,
    Market Market,
    int SignedQuantity,
    decimal AverageCost,
    decimal? AverageFxRateBaseToDisplay,
    int UnrecordedFxRateFillCount);
