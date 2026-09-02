using AiStockTrading.Shared.Contracts.Trading;

namespace ReportService.Domain;

// FR-06, FR-16, #611, 04_report-templates §数値の定義（為替差損益・円換算）, 05_trading-assumptions §3, IADR-0285 決定3・決定4:
// 期間の約定から為替差損益の明細（FxTranslationEntry）を組み立てる**純関数**（決定的・副作用なし・LLM に触れさせない）。
//
// 対象は**基準通貨（USD）建てで、表示通貨（JPY）建てでない**約定＝米国株である。計画は「円換算 | **米国株は**前提条件の
// 為替評価方法に従い円換算」と定めており、日本株は円建てで円換算を要しない（差損益を生まない）。
//
// 畳み込みは SignedInventory（IADR-0033・平均取得単価法）と同じ規則で、(銘柄, 市場) ごとに **USD 取得原価**と
// **円換算取得原価**（＝認識時レートの加重平均）を持つ。
//   - 減少（決済）: 減少分の USD 原価を明細にする——認識時レート（加重平均）から**決済約定の認識時レート**への再測定。
//   - 期末: 残る建玉の USD 原価を明細にする——認識時レートから**期末レート**への再測定。
//   - ロングは +、ショートは −（USD 建て負債の再測定は符号が逆）。
//
// 🔴 **約定ごとに約定代金を明細にしない。** 期間内に同じ建玉を建てて決済すると両脚を二重に数える
// （$1,000 を 150 円で買い $1,100 を 155 円で売り期末 160 円: 約定ごとでは 15,500 円、決済で確定した差損益は 5,000 円）。
//
// 🔴 **認識時レートが未記録の対象約定が 1 件でもあれば集計しない（null）。** 畳み込みは状態を持つため、
// 未記録の約定を落として残りだけ集計すると別の数値になる（先の買いを落とすと後の売りが幻のショートになる）。
// **黙って落とさない**——未記録の件数を返し、描画が明記する（IADR-0271 決定2 と同じ規律）。既存行は推定で埋めない。
//
// 既知の限界（PnlAggregator と同じ）: 報告書は**期間内の約定しか受け取らない**ため、前期間に建てた建玉の決済は
// 在庫 0 からの反対売買として畳み込まれる。円建て表示は参考値であり（計画 §3）、統制の判定には用いない。
public static class FxTranslationBuilder
{
    /// <summary>報告書の表示通貨（計画 §3「基準通貨〔表示〕= JPY」）。</summary>
    public const Currency DisplayCurrency = Currency.Jpy;

    /// <summary>
    /// 期間の約定から為替差損益を組み立てる。
    /// <para>
    /// <paramref name="periodEnd"/> が要るのは**期末に建玉が残るときだけ**である。残らなければ期末レートが無くても
    /// 集計できる（未供給へ倒さない）。対象約定が 1 件も無ければ「0 円（明細 0 件）」——事実であり未供給ではない。
    /// </para>
    /// </summary>
    public static FxTranslationBuildResult Build(IReadOnlyList<PeriodTradeFill> fills, PeriodEndFxRate? periodEnd)
    {
        ArgumentNullException.ThrowIfNull(fills);

        var translatable = fills.Where(IsTranslatable).OrderBy(f => f.ExecutedAt).ToList();

        var unrecorded = translatable.Count(f => f.FxRateBaseToDisplay is not > 0m);
        if (unrecorded > 0)
            return new FxTranslationBuildResult(null, unrecorded);

        var entries = new List<FxTranslationEntry>();
        var lots = new Dictionary<(string Symbol, Market Market), Lot>();

        foreach (var fill in translatable)
        {
            var key = (fill.Symbol, fill.Market);
            var signedQuantity = fill.Side == TradeSide.Buy ? fill.Quantity : -fill.Quantity;
            lots.TryGetValue(key, out var lot);
            lots[key] = Apply(lot, signedQuantity, fill.Price, fill.FxRateBaseToDisplay!.Value, entries);
        }

        var open = lots.Values.Where(l => l.Quantity != 0 && l.CostBase > 0m).ToList();
        if (open.Count == 0)
            return new FxTranslationBuildResult(FxTranslationAggregator.Aggregate(entries), 0);

        // 期末に建玉が残る＝期末レートが要る。無ければ**未供給**（推定しない・0 円と書かない）。
        if (periodEnd is null)
            return new FxTranslationBuildResult(null, 0);

        foreach (var lot in open)
            entries.Add(new FxTranslationEntry(Signed(lot), lot.AverageRate, periodEnd.JpyPerUsd));

        var summary = FxTranslationAggregator.Aggregate(entries) with
        {
            PeriodEndRate = periodEnd.JpyPerUsd,
            PeriodEndRateAsOf = periodEnd.AsOf,
        };
        return new FxTranslationBuildResult(summary, 0);
    }

    // 対象＝市場通貨が基準通貨（USD）であり、かつ表示通貨（JPY）ではない約定。
    // 市場の追加で通貨が増えたときに黙って対象外になるのではなく、MarketCurrency.Of が落とす（既定へ倒さない）。
    private static bool IsTranslatable(PeriodTradeFill fill)
    {
        var currency = MarketCurrency.Of(fill.Market);
        return currency == MarketCurrency.Base && currency != DisplayCurrency;
    }

    // 符号付き在庫へ 1 約定を適用し、決済分の明細を積む。SignedInventory と同じ分岐（新規建て／建て増し／減少・反転）で、
    // 平均取得単価に加えて**認識時レートの原価加重平均**を持ち回る。
    private static Lot Apply(Lot current, int signedQuantity, decimal price, decimal rate, List<FxTranslationEntry> entries)
    {
        if (current.Quantity == 0)
            return new Lot(signedQuantity, price, rate);

        if (Math.Sign(current.Quantity) == Math.Sign(signedQuantity))
        {
            // 同方向＝建て増し。取得単価は SignedInventory と同じ加重平均、認識時レートは USD 原価で加重する。
            var heldBase = Math.Abs(current.Quantity) * current.AverageCost;
            var addedBase = Math.Abs(signedQuantity) * price;
            var newQuantity = current.Quantity + signedQuantity;
            var averageCost = (heldBase + addedBase) / Math.Abs(newQuantity);

            // 等しいレートの加重平均はそのレートである——除算の丸めで「レートが変わらなければ 0」の不変条件を崩さない。
            var averageRate = rate == current.AverageRate || heldBase + addedBase == 0m
                ? rate
                : (heldBase * current.AverageRate + addedBase * rate) / (heldBase + addedBase);

            return new Lot(newQuantity, averageCost, averageRate);
        }

        // 反対方向＝減少（と反転の可能性）。減少分の USD 原価を、認識時レート（加重平均）から決済時レートへ再測定する。
        var reduce = Math.Min(Math.Abs(current.Quantity), Math.Abs(signedQuantity));
        var closedBase = reduce * current.AverageCost;
        if (closedBase > 0m)
        {
            var sign = current.Quantity > 0 ? 1m : -1m;
            entries.Add(new FxTranslationEntry(sign * closedBase, current.AverageRate, rate));
        }

        var remaining = current.Quantity + signedQuantity;
        if (remaining == 0)
            return default;

        // 反転: 元の建玉を全決済し、余りを新規建玉として決済約定の単価・レートで建てる。
        // 同方向のまま一部決済: 取得単価・認識時レートは不変（SignedInventory と同じ規則）。
        return Math.Sign(remaining) != Math.Sign(current.Quantity)
            ? new Lot(remaining, price, rate)
            : new Lot(remaining, current.AverageCost, current.AverageRate);
    }

    private static decimal Signed(Lot lot) => lot.Quantity > 0 ? lot.CostBase : -lot.CostBase;

    // 符号付き数量（+ ロング / − ショート）・平均取得単価（USD・SignedInventory と同じ）・認識時レートの原価加重平均（円/ドル）。
    private readonly record struct Lot(int Quantity, decimal AverageCost, decimal AverageRate)
    {
        /// <summary>USD 取得原価（非負）。</summary>
        public decimal CostBase => Math.Abs(Quantity) * AverageCost;
    }
}

/// <summary>
/// FR-06, FR-16, #611, IADR-0285 決定3: 組み立ての結果。
/// </summary>
/// <param name="Summary">集計結果。<b><c>null</c> は「供給されていない」</b>（0 円ではない）。</param>
/// <param name="UnrecordedFillCount">
/// 認識時レートが未記録だった対象（USD 建て）約定の件数。0 より大きければ <paramref name="Summary"/> は必ず <c>null</c> であり、
/// 描画は件数を明記する（黙って落とさない）。
/// </param>
public sealed record FxTranslationBuildResult(FxTranslationSummary? Summary, int UnrecordedFillCount);
