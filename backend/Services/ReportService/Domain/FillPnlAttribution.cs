using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace ReportService.Domain;

// FR-06, FR-07, FR-16, 04_report-templates 週報 §2/§3・月報 §2, #615, IADR-0301:
// **約定単位の損益帰属**（純関数）。期間全体を 1 回だけ畳み込み、決済（在庫が減る約定）の実現損益を
// その約定へ結び付ける。日・週・市場・方向のどの軸へも、ここから**再畳み込みなしに**集計できる。
//
// 🔴 **期間を切って PnlAggregator.Aggregate を呼び直してはならない。**
// PnlAggregator は期間全体を SignedInventory.Apply（IADR-0033）で畳み込む。日・週・市場でスライスして
// 呼び直すと、**持ち越し建玉の平均取得単価がスライス内に存在しない**ため、内訳の合計が §1 サマリの合計と
// 一致しなくなる。しかも**各スライスは自分の中では整合しているため、全テストが緑のままそうなる。**
//
// 🔴 **数値はコード集計値・文章は記録の転記であり、いずれも LLM に作らせない**（FR-16・IADR-0251）。
// 費用は `CostCalculator.EstimateOneWayCost`（PnlAggregator と**同じ関数**）、実現損益は
// `SignedInventory.Apply`（PnlAggregator と**同じ畳み込み**）、判断根拠は監査台帳の記録の転記である。
public sealed record FillPnlAttribution(
    /// <summary>畳み込み順（1 起点）。同値の並び替えを入力順へ依存させないための最終キー。</summary>
    int Sequence,

    /// <summary>約定時刻（供給元の値そのまま）。</summary>
    DateTimeOffset ExecutedAt,

    /// <summary>帰属日（**JST**。日別・週別の集計キー。日報 §2 の時刻表記と同じ基準）。</summary>
    DateOnly SessionDateJst,

    /// <summary>市場（市場別の集計キー）。</summary>
    Market Market,

    /// <summary>銘柄コード（台帳は名称を持たない）。</summary>
    string Symbol,

    /// <summary>
    /// 売買方向。<b>方向別（ロング/ショート）の集計キーはこの列から一意に決まる</b>——決済は必ず反対方向の
    /// 約定であるため、<c>Realizing &amp;&amp; Side == Sell</c> ⇒ ロングの決済、<c>Realizing &amp;&amp; Side == Buy</c> ⇒
    /// ショートの決済である。<b>後続の集計が畳み込みをやり直す必要は無い。</b>
    /// </summary>
    TradeSide Side,

    /// <summary>約定数量（&gt; 0）。</summary>
    int Quantity,

    /// <summary>約定単価（基準通貨建て・PnlAggregator と同じ入力）。</summary>
    decimal Price,

    /// <summary>この約定に掛かる概算費用（CostCalculator。**PnlAggregator と同じ関数**）。</summary>
    decimal Cost,

    /// <summary>
    /// この約定で実現した損益（<b>税引前・費用前</b>）。決済でない約定は 0（<b>事実であり未供給ではない</b>）。
    /// <para>🔴 <b>源泉徴収税額は期間合計にのみ課され、約定単位へ配分する規則が無い</b>（日報 §2 と同じ理由）。
    /// ここに税を按分して載せない。</para>
    /// </summary>
    decimal RealizedPnlGross,

    /// <summary>在庫が減った（決済が発生した）か。勝率・ハイライトの母集合はこれが <c>true</c> の約定である。</summary>
    bool Realizing,

    /// <summary>
    /// 記録された判断根拠（<c>DecisionId</c> 引き）。<c>null</c>＝相関できなかった（未供給）。
    /// <b>報告書生成時に文章を作らない</b>（TradeHistoryViewBuilder と同じ規則）。
    /// </summary>
    string? Rationale);

// 04_report-templates 週報 §2「日別推移」の 1 行。**約定が 1 件も無い日は行そのものが存在しない**
//（休場日と「実現損益 0 の営業日」を区別できる記録源が無いため。IADR-0301 決定2）。
public sealed record DailyPnlRow(
    DateOnly SessionDateJst,

    /// <summary>当日の決済損益の合計（税引前・費用前）。</summary>
    decimal RealizedPnlGross,

    /// <summary>当日の約定に掛かる概算費用の合計。</summary>
    decimal Cost,

    /// <summary>当日の約定件数（決済に至らない新規建てを含む）。</summary>
    int FillCount,

    /// <summary>当日の決済件数。</summary>
    int RealizingCount,

    /// <summary>
    /// 当日の実現損益への<b>寄与が最大の決済</b>（絶対値が最大のもの）。<c>null</c>＝当日は決済が無い。
    /// <b>要因の説明（散文）ではない</b>——散文の記録源は存在しない。
    /// </summary>
    FillPnlAttribution? LargestContributor)
{
    /// <summary>当日の実現損益（<b>税引前・費用込み</b>）。税は期間合計にのみ課されるため日へ配分しない。</summary>
    public decimal RealizedPnlAfterCost => RealizedPnlGross - Cost;
}

// 04_report-templates 週報 §3「ハイライト取引」の抽出結果。**決済が 0 件なら両方 null**（「損益 0」ではない）。
// 決済が 1 件だけなら Best と Worst は**同一の約定**になる（呼び出し側が明記する。隠すと 2 件あったように読める）。
public sealed record TradeHighlights(FillPnlAttribution? Best, FillPnlAttribution? Worst);

public static class FillPnlAttributionBuilder
{
    /// <summary>
    /// 約定列を<b>期間全体で 1 回だけ</b>畳み込み、約定単位の損益帰属を作る。
    /// </summary>
    /// <param name="fills">集計対象期間の約定（順序は問わない。約定時刻の昇順へ並べ替える）。</param>
    /// <param name="assumptions">費用の概算に用いる全体前提条件（FR-17）。</param>
    /// <param name="rationales">
    /// <c>DecisionId</c> → 記録された判断根拠。<c>null</c>＝判断記録そのものが未供給。
    /// 辞書に無い <c>DecisionId</c> と空文字の根拠は<b>その約定だけ</b>未供給になる。
    /// </param>
    public static IReadOnlyList<FillPnlAttribution> Build(
        IReadOnlyList<PeriodTradeFill> fills,
        TradingAssumptions assumptions,
        IReadOnlyDictionary<Guid, string>? rationales)
    {
        ArgumentNullException.ThrowIfNull(fills);
        ArgumentNullException.ThrowIfNull(assumptions);

        var positions = new Dictionary<(string Symbol, Market Market), InventoryLot>();
        var entries = new List<FillPnlAttribution>(fills.Count);
        var sequence = 0;

        // 🔴 **並べ替えは PnlAggregator / TradeHistoryViewBuilder と同一**（同じ畳み込み順序でなければ
        // 内訳の合計が §1 サマリと一致しない）。
        foreach (var fill in fills.OrderBy(f => f.ExecutedAt))
        {
            sequence++;

            var key = (fill.Symbol, fill.Market);
            var signedQuantity = fill.Side == TradeSide.Buy ? fill.Quantity : -fill.Quantity;
            positions.TryGetValue(key, out var lot);
            var applied = SignedInventory.Apply(lot, signedQuantity, fill.Price);
            positions[key] = applied.Lot;

            entries.Add(new FillPnlAttribution(
                sequence,
                fill.ExecutedAt,
                DateOnly.FromDateTime(fill.ExecutedAt.ToOffset(ReportSchedule.JstOffset).DateTime),
                fill.Market,
                fill.Symbol,
                fill.Side,
                fill.Quantity,
                fill.Price,
                CostCalculator.EstimateOneWayCost(assumptions, fill.Market, fill.Quantity * fill.Price),
                // 在庫が減らない約定の実現損益は 0（事実）。未供給ではない。
                applied.Reduced ? applied.RealizedPnl : 0m,
                applied.Reduced,
                Rationale(rationales, fill.DecisionId)));
        }

        return entries;
    }

    /// <summary>
    /// 日別（JST）へ集計する。<b>約定のあった日だけ</b>が行になる（IADR-0301 決定2）。
    /// 並びは日付の昇順（決定的）。
    /// </summary>
    public static IReadOnlyList<DailyPnlRow> ByDay(IReadOnlyList<FillPnlAttribution> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return
        [
            .. entries
                .GroupBy(e => e.SessionDateJst)
                .OrderBy(g => g.Key)
                .Select(g => new DailyPnlRow(
                    g.Key,
                    g.Sum(e => e.RealizedPnlGross),
                    g.Sum(e => e.Cost),
                    g.Count(),
                    g.Count(e => e.Realizing),
                    // 寄与が最大の決済＝実現損益の絶対値が最大のもの。同値は全順序（Rank）で決める。
                    g.Where(e => e.Realizing)
                        .OrderByDescending(e => Math.Abs(e.RealizedPnlGross))
                        .ThenBy(e => e, TieBreak)
                        .FirstOrDefault())),
        ];
    }

    /// <summary>
    /// 決済の中から最良・最悪を選ぶ。<b>決済が 0 件なら両方 <c>null</c></b>（「損益 0」ではない）。
    /// 決済が 1 件だけなら<b>同一の約定</b>が両方に入る。
    /// </summary>
    public static TradeHighlights Highlights(IReadOnlyList<FillPnlAttribution> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var realizing = entries.Where(e => e.Realizing).ToList();
        if (realizing.Count == 0)
            return new TradeHighlights(null, null);

        // 🔴 **同値でも並びが変わらないこと**（IADR-0301 決定1）。実現損益が同じ決済が複数あるとき、
        // 入力列の順序で結果が変わると、同じ週報を作り直すたびにハイライトの銘柄が入れ替わる。
        var best = realizing.OrderByDescending(e => e.RealizedPnlGross).ThenBy(e => e, TieBreak).First();
        var worst = realizing.OrderBy(e => e.RealizedPnlGross).ThenBy(e => e, TieBreak).First();
        return new TradeHighlights(best, worst);
    }

    /// <summary>
    /// 同値時の全順序（<b>約定時刻 → 銘柄コード〔序数〕→ 市場 → 畳み込み順序</b>）。
    /// <b>辞書・ハッシュの列挙順序に依存しない。</b>
    /// </summary>
    private static readonly IComparer<FillPnlAttribution> TieBreak =
        Comparer<FillPnlAttribution>.Create((a, b) =>
        {
            var byTime = a.ExecutedAt.CompareTo(b.ExecutedAt);
            if (byTime != 0)
                return byTime;

            var bySymbol = string.CompareOrdinal(a.Symbol, b.Symbol);
            if (bySymbol != 0)
                return bySymbol;

            var byMarket = a.Market.CompareTo(b.Market);
            return byMarket != 0 ? byMarket : a.Sequence.CompareTo(b.Sequence);
        });

    // 相関できない約定（DecisionId が空・辞書に無い）と、空の根拠は未供給にする。**近い記録で代替しない。**
    private static string? Rationale(IReadOnlyDictionary<Guid, string>? rationales, Guid decisionId)
    {
        if (rationales is null || decisionId == Guid.Empty)
            return null;

        return rationales.TryGetValue(decisionId, out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
    }
}
