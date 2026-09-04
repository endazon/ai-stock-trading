using AiStockTrading.Shared.Contracts.Trading;

namespace ReportService.Domain;

// FR-06, FR-07, FR-16, 04_report-templates 月報 §2「週別・市場別の内訳」, #615, IADR-0306:
// **週別・市場別・建玉方向別の内訳**（純関数）。
//
// 🔴 **期間・区分を切って PnlAggregator.Aggregate を呼び直さない**（IADR-0301 決定1）。入力は
// 期間全体を 1 回だけ畳み込んだ約定単位の帰属（FillPnlAttribution）であり、3 表とも**それを数え直すだけ**である。
// スライスして畳み込み直すと、**持ち越し建玉の平均取得単価がスライス内に存在しない**ため、内訳の合計が
// §1 サマリと一致しなくなる——しかも**各スライスは自分の中では整合しているため、全テストが緑のままそうなる。**
//
// 🔴 **帰属へ列を 1 つも足していない。** 週＝`SessionDateJst` の ISO 週、市場＝`Market`、
// 方向＝`Realizing` と `Side` の組み合わせ（下記 `IsLong`）から導ける。

// 04_report-templates 月報 §2 表 1「週別」の 1 行。**約定が 1 件も無い週は行そのものが存在しない**
//（週報 §2 の日別行と同じ理由。営業日カレンダーを持たない。IADR-0301 決定2）。
public sealed record WeeklyPnlRow(
    /// <summary>ISO 週ラベル（<c>2026-W35</c>）。<b>年を含む</b>ため年跨ぎでも取り違えない。</summary>
    string WeekLabel,

    /// <summary>当週の決済損益の合計（税引前・費用前）。</summary>
    decimal RealizedPnlGross,

    /// <summary>当週の約定に掛かる概算費用の合計。</summary>
    decimal Cost,

    /// <summary>当週の約定件数（決済に至らない新規建てを含む）。</summary>
    int FillCount,

    /// <summary>当週の決済件数。</summary>
    int RealizingCount,

    /// <summary>
    /// 当週の実現損益への<b>寄与が最大の決済</b>（絶対値が最大）。<c>null</c>＝当週は決済が無い。
    /// <b>要因の説明（散文）ではない</b>——散文の記録源は存在しない（IADR-0301 決定5 と同じ）。
    /// </summary>
    FillPnlAttribution? LargestContributor)
{
    /// <summary>当週の実現損益（<b>税引前・費用込み</b>）。税は期間合計にのみ課されるため週へ配分しない。</summary>
    public decimal RealizedPnlAfterCost => RealizedPnlGross - Cost;
}

// 04_report-templates 月報 §2 表 2「市場別」の 1 行。
//
// 🔴 **約定が 1 件も無い市場も行を出す**（計画が行を固定している）。ただし
// <see cref="FillCount"/> が 0 のとき、描画側は「（当月の約定なし）」と明記する——
// **`0` を「取引して収支が 0 だった」と読ませない。**
public sealed record MarketPnlRow(
    Market Market,
    decimal RealizedPnlGross,
    decimal Cost,
    int FillCount,

    /// <summary>当該市場で実現損益が最大の銘柄（決済のみが母集合）。<c>null</c>＝決済が無い。</summary>
    (string Symbol, decimal RealizedPnlGross)? Best,

    /// <summary>当該市場で実現損益が最小の銘柄（同上）。決済が 1 銘柄だけなら <see cref="Best"/> と同一。</summary>
    (string Symbol, decimal RealizedPnlGross)? Worst);

// 04_report-templates 月報 §2 表 3「建玉の方向別」の 1 行。
public sealed record DirectionPnlRow(
    bool IsLong,
    decimal RealizedPnlGross,
    decimal Cost,
    int FillCount,
    int RealizingCount,
    int WinningCount);

public static class PeriodBreakdownBuilder
{
    /// <summary>
    /// 🔴 <b>建玉の方向を帰属から導く（列を足さない）。</b>
    /// <para>
    /// <see cref="SignedInventory.Apply"/> は<b>在庫 0 か同符号なら <c>Reduced=false</c>／反対符号のときだけ
    /// <c>Reduced=true</c></b> を返す。したがって
    /// </para>
    /// <list type="bullet">
    /// <item><c>Realizing</c> ⇒ 直前の在庫は約定と<b>反対符号</b> ⇒ 決済された建玉は Sell ならロング・Buy ならショート。</item>
    /// <item><c>!Realizing</c> ⇒ 直前の在庫は 0 か<b>同符号</b> ⇒ 建てた建玉は Buy ならロング・Sell ならショート。</item>
    /// </list>
    /// <para>
    /// 反転（ロングに対する大きな売り）は 1 約定で「ロングの全決済＋ショートの新規建て」を兼ねるが、
    /// <c>Realizing</c> かつ Sell なので<b>ロング側に数える</b>——<b>1 約定を 2 行へ割らない</b>
    /// （割ると取引数の合計が §1 サマリと合わなくなる）。
    /// </para>
    /// </summary>
    public static bool IsLong(FillPnlAttribution entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Realizing ? entry.Side == TradeSide.Sell : entry.Side == TradeSide.Buy;
    }

    /// <summary>
    /// ISO 週（月曜起点）へ集計する。<b>約定のあった週だけ</b>が行になる。並びは週ラベルの昇順（決定的）。
    /// </summary>
    public static IReadOnlyList<WeeklyPnlRow> ByWeek(IReadOnlyList<FillPnlAttribution> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return
        [
            .. entries
                // 週の識別子は報告書の自然キーと同じ導出（ReportPeriod）を使う——別の週割りを持たない。
                .GroupBy(e => ReportPeriod.Label(ReportKind.Weekly, e.SessionDateJst), StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new WeeklyPnlRow(
                    g.Key,
                    g.Sum(e => e.RealizedPnlGross),
                    g.Sum(e => e.Cost),
                    g.Count(),
                    g.Count(e => e.Realizing),
                    g.Where(e => e.Realizing)
                        .OrderByDescending(e => Math.Abs(e.RealizedPnlGross))
                        .ThenBy(e => e.Sequence)
                        .FirstOrDefault())),
        ];
    }

    /// <summary>
    /// 市場へ集計する。<b>約定が 1 件も無い市場も行を返す</b>（計画が行を固定しているため）。
    /// 並びは <see cref="Market"/> の列挙順（決定的）。
    /// </summary>
    public static IReadOnlyList<MarketPnlRow> ByMarket(IReadOnlyList<FillPnlAttribution> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return
        [
            .. Enum.GetValues<Market>().Select(market =>
            {
                var rows = entries.Where(e => e.Market == market).ToList();

                // 主要銘柄は**決済**を銘柄で集計したもの（新規建ては実現損益 0 であり、
                // 母集合へ入れると「損益 0 の銘柄が上位」という無意味な行になる）。
                var bySymbol = rows
                    .Where(e => e.Realizing)
                    .GroupBy(e => e.Symbol, StringComparer.Ordinal)
                    .Select(g => (Symbol: g.Key, RealizedPnlGross: g.Sum(e => e.RealizedPnlGross)))
                    .ToList();

                (string, decimal)? best = bySymbol.Count == 0
                    ? null
                    : bySymbol
                        .OrderByDescending(s => s.RealizedPnlGross)
                        .ThenBy(s => s.Symbol, StringComparer.Ordinal)
                        .First();
                (string, decimal)? worst = bySymbol.Count == 0
                    ? null
                    : bySymbol
                        .OrderBy(s => s.RealizedPnlGross)
                        .ThenBy(s => s.Symbol, StringComparer.Ordinal)
                        .First();

                return new MarketPnlRow(
                    market,
                    rows.Sum(e => e.RealizedPnlGross),
                    rows.Sum(e => e.Cost),
                    rows.Count,
                    best,
                    worst);
            }),
        ];
    }

    /// <summary>
    /// 建玉の方向（ロング / ショート）へ集計する。<b>常に 2 行</b>（計画が行を固定しているため）。
    /// 並びはロング → ショート（計画の表の順）。
    /// </summary>
    public static IReadOnlyList<DirectionPnlRow> ByDirection(IReadOnlyList<FillPnlAttribution> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return
        [
            .. new[] { true, false }.Select(isLong =>
            {
                var rows = entries.Where(e => IsLong(e) == isLong).ToList();
                return new DirectionPnlRow(
                    isLong,
                    rows.Sum(e => e.RealizedPnlGross),
                    rows.Sum(e => e.Cost),
                    rows.Count,
                    rows.Count(e => e.Realizing),
                    rows.Count(e => e.Realizing && e.RealizedPnlGross > 0m));
            }),
        ];
    }
}
