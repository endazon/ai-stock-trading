using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement.GetOpeningInventory;

// FR-06, FR-16, #1181, IADR-0493 決定 1・2: 取引台帳を**指定した市場の指定した現地取引日より前（排他）**まで畳んだ在庫
// （期間開始時点の在庫）を返す純関数。報告書サービスが期間より前に建てた建玉の決済の実現損益を算定するため
// s2s 同期照会する GET /risk-controls/opening-inventory・gRPC RiskControlsRead/GetOpeningInventory の実体。
//
// 🔴 **畳み込みは射影（PortfolioProjection）と同じ 1 行の入口 `ApplyToLot` を使う**（平均取得単価法の単一情報源は
// SignedInventory。IADR-0033）。取得原価を報告書側で別に畳み直さない——2 か所で畳むと規則がずれ得る（IADR-0493 §却下した案 b）。
//   - 約定は基準通貨（USD）の単価（`PriceInBase`）で畳む。報告書は約定を同じ式（単価 × FxRateToBase）で受け取るため、
//     ここで返す平均取得単価をそのまま報告書の在庫の初期値に置ける（IADR-0107 の二重畳み込みの基準通貨側）。
//   - 乖離の取り込み行（台帳では ExecutedAt＝取り込み日時で約定列に合流する）は**その時点の平均取得単価で数量だけ**減らす（IADR-0350 決定 3）。
//
// 🔴 **境界は市場の現地取引日**（PortfolioProjection.TradeDate＝TradingDay.Of(instant, market)）。報告書の窓（IADR-0492）も
// 約定を同じ取引日で絞るため、「窓の下端の取引日より前」と「窓に入る」は重ならず隙間も無い（取引日 = before の行は在庫に入らない）。
// 時刻（JST 0 時など）では切らない——米国の ET 深夜の約定が JST では翌日になり、窓の行と二重に数えるか取りこぼす。
public static class OpeningInventoryQuery
{
    /// <summary>
    /// FR-06, FR-16, #1186, IADR-0506 決定 3: 台帳から<b>市場と約定時刻の外包（<see cref="LedgerScanBounds"/>）だけ</b>を読み、
    /// 下の純関数で正確に畳む。REST・gRPC の入口はこちらを呼ぶ（台帳の全行を読まない）。結果は全行を畳んだときと同じである。
    /// </summary>
    public static IReadOnlyList<OpeningInventoryView> AsOf(
        IPortfolioLedgerStore ledger, Market market, DateOnly beforeTradingDay)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        var candidates = ledger.GetFillsExecutedBetween(
            market, executedAtOrAfter: null, LedgerScanBounds.ExecutedBeforeForTradingDayBefore(beforeTradingDay));
        return AsOf(candidates, market, beforeTradingDay);
    }

    /// <summary>
    /// <paramref name="market"/> の台帳行（約定と取り込み）のうち、取引日が <paramref name="beforeTradingDay"/> より前のものを
    /// 約定時刻の昇順で畳み、数量が 0 でない銘柄を銘柄コードの序数順で返す（決定的）。
    /// </summary>
    public static IReadOnlyList<OpeningInventoryView> AsOf(
        IReadOnlyList<LedgerFill> fills, Market market, DateOnly beforeTradingDay)
    {
        ArgumentNullException.ThrowIfNull(fills);

        var lots = new Dictionary<string, Lot>(StringComparer.Ordinal);

        // 射影と同じ並び（約定時刻の昇順・安定ソート）。同時刻の並びは台帳の読み出し順に従う（射影と同じ）。
        foreach (var fill in fills
                     .Where(f => f.Market == market && PortfolioProjection.TradeDate(f.ExecutedAt, f.Market) < beforeTradingDay)
                     .OrderBy(f => f.ExecutedAt))
        {
            lots.TryGetValue(fill.Symbol, out var lot);
            lots[fill.Symbol] = Apply(lot, fill);
        }

        return
        [
            .. lots
                .Where(e => e.Value.Inventory.Quantity != 0)
                .OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => new OpeningInventoryView(
                    e.Key,
                    market,
                    e.Value.Inventory.Quantity > 0 ? TradeSide.Buy : TradeSide.Sell,
                    Math.Abs(e.Value.Inventory.Quantity),
                    e.Value.Inventory.AverageCost,
                    e.Value.UnrecordedRateFills > 0 ? null : e.Value.AverageRate,
                    e.Value.UnrecordedRateFills)),
        ];
    }

    // 1 行を在庫へ適用する。数量と基準通貨の平均取得単価は射影と同じ ApplyToLot、認識時レート（1 USD あたりの円）は
    // 報告書の為替差損益（FxTranslationBuilder）と同じ規則で**基準通貨の原価で加重平均**する（IADR-0286 決定 3）。
    private static Lot Apply(Lot current, LedgerFill fill)
    {
        var signedQuantity = fill.Side == TradeSide.Buy ? fill.Quantity : -fill.Quantity;
        var before = current.Inventory;
        var after = PortfolioProjection.ApplyToLot(before, signedQuantity, fill.PriceInBase, fill.IsDriftAdoption).Lot;

        if (after.Quantity == 0)
            return default; // 全決済: 認識時レートも未記録の数も消える（次の建玉は数え直す）。

        // 新規・反転: 残りはこの約定だけの建玉である（取り込み行は減らす方向に限るため、ここへは来ない。来たら未記録に倒す）。
        if (before.Quantity == 0 || Math.Sign(before.Quantity) != Math.Sign(after.Quantity))
            return Fresh(after, fill);

        // 一部決済・取り込み: 取得単価と認識時レートは不変（SignedInventory の「同方向のまま一部決済」と同じ規則）。
        if (Math.Abs(after.Quantity) < Math.Abs(before.Quantity))
            return current with { Inventory = after };

        // 建て増し: 認識時レートを基準通貨の原価で加重する。未記録の約定が 1 件でも建玉に入れば平均は作れない（件数を返す）。
        var rate = fill.FxRateBaseToDisplay;
        if (fill.IsDriftAdoption || rate is not > 0m || current.UnrecordedRateFills > 0)
            return new Lot(after, 0m, current.UnrecordedRateFills + (fill.IsDriftAdoption || rate is not > 0m ? 1 : 0));

        var heldBase = Math.Abs(before.Quantity) * before.AverageCost;
        var addedBase = Math.Abs(signedQuantity) * fill.PriceInBase;
        // 等しいレートの加重平均はそのレートである（除算の丸めで「レートが変わらなければ為替差損益 0」を崩さない。報告書と同じ）。
        var averageRate = rate.Value == current.AverageRate || heldBase + addedBase == 0m
            ? rate.Value
            : (heldBase * current.AverageRate + addedBase * rate.Value) / (heldBase + addedBase);
        return new Lot(after, averageRate, 0);
    }

    private static Lot Fresh(InventoryLot inventory, LedgerFill fill) =>
        !fill.IsDriftAdoption && fill.FxRateBaseToDisplay is > 0m and var rate
            ? new Lot(inventory, rate, 0)
            : new Lot(inventory, 0m, 1);

    // 符号付き在庫（基準通貨の平均取得単価）・認識時レートの加重平均・建玉に残る「認識時レートが未記録の約定」の数。
    private readonly record struct Lot(InventoryLot Inventory, decimal AverageRate, int UnrecordedRateFills);
}

/// <summary>
/// FR-06, FR-16, #1181, IADR-0493 決定 2: 期間開始時点の在庫 1 銘柄ぶんの wire 形（報告書の在庫の初期値）。
/// </summary>
/// <param name="Side">建玉の向き（ロング＝Buy・ショート＝Sell）。</param>
/// <param name="Quantity">数量（&gt; 0）。</param>
/// <param name="AverageCostInBase">
/// <b>基準通貨（USD）建ての平均取得単価</b>（平均取得単価法・約定時レートで換算済み）。報告書の約定単価と同じ通貨である。
/// </param>
/// <param name="AverageFxRateBaseToDisplay">
/// 認識時レート（1 USD あたりの円）の基準通貨の原価による加重平均。<b><c>null</c>＝建玉に未記録の約定が含まれ、平均を作れない</b>
/// （0・1 へ倒さない。報告書は為替差損益を未供給にする）。
/// </param>
/// <param name="UnrecordedFxRateFillCount">建玉に残る、認識時レートが未記録の台帳行の数（0 なら <paramref name="AverageFxRateBaseToDisplay"/> は非 null）。</param>
public sealed record OpeningInventoryView(
    string Symbol,
    Market Market,
    TradeSide Side,
    int Quantity,
    decimal AverageCostInBase,
    decimal? AverageFxRateBaseToDisplay,
    int UnrecordedFxRateFillCount);
