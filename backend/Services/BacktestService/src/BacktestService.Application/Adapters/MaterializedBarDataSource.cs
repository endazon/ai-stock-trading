using AiStockTrading.Backtest.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Backtest.Application;

// FR-15, #208, IADR-0105: 実過去データ源から取得済みのバーを IBarDataSource として供給するスナップショット（本番経路）。
//
// InMemoryBarDataSource（テスト・検証用の決定的スタブ）と役割を分ける。こちらは「外部から取得した実データを
// 1 回だけ固定して評価へ渡す」ための本番実装であり、IBarDataSource の契約が求める正規化
// （同一 (Symbol, Market, Date) を 1 本に畳む・安定した列挙順）を担う単一情報源でもある。
public sealed class MaterializedBarDataSource : IBarDataSource
{
    private readonly IReadOnlyList<PriceBar> _bars;

    private MaterializedBarDataSource(IReadOnlyList<PriceBar> bars, IReadOnlyList<HistoricalBarGap> gaps)
    {
        _bars = bars;
        Gaps = gaps;
    }

    /// <summary>取得できなかった銘柄と理由。欠測のまま合格させないための材料として保持する。</summary>
    public IReadOnlyList<HistoricalBarGap> Gaps { get; }

    /// <summary>取得済みのバーからスナップショットを作る（正規化つき）。</summary>
    public static MaterializedBarDataSource FromBars(
        IEnumerable<PriceBar> bars, IReadOnlyList<HistoricalBarGap>? gaps = null)
    {
        ArgumentNullException.ThrowIfNull(bars);
        return new MaterializedBarDataSource(Normalize(bars), gaps ?? []);
    }

    /// <summary>
    /// PIT ユニバースから取得対象銘柄を導出し、実過去データ源から取得してスナップショットを作る。
    /// 取得は 1 回だけ行い、以降の評価（シミュレーション・ウォークフォワード）は純粋な参照に閉じる。
    /// </summary>
    public static async Task<MaterializedBarDataSource> LoadAsync(
        IHistoricalBarSource source,
        SecurityUniverse universe,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(universe);

        // 取得対象は「期間内に一度でも構成銘柄だった銘柄」＝上場廃止銘柄を含む（生存者バイアス排除）。
        var symbols = universe.MembersBetween(from, to);
        if (symbols.Count == 0)
            return new MaterializedBarDataSource([], []);

        var load = await source.LoadBarsAsync(symbols, from, to, cancellationToken).ConfigureAwait(false);
        return new MaterializedBarDataSource(Normalize(load.Bars), load.Gaps);
    }

    public IReadOnlyList<PriceBar> GetBars(DateOnly from, DateOnly to) =>
        _bars.Where(b => b.Date >= from && b.Date <= to).ToList();

    // 同一 (Symbol, Market, Date) は 1 本に畳む（後勝ち＝BacktestSimulator の重複時の採用規則に合わせる）。
    // 列挙順は日付 → 銘柄 → 市場の安定順にし、データ源の返却順の揺れが結果へ漏れないようにする。
    private static IReadOnlyList<PriceBar> Normalize(IEnumerable<PriceBar> bars)
    {
        var deduped = new Dictionary<(string Symbol, Market Market, DateOnly Date), PriceBar>();
        foreach (var bar in bars)
            deduped[(bar.Symbol, bar.Market, bar.Date)] = bar;

        return [.. deduped.Values
            .OrderBy(b => b.Date)
            .ThenBy(b => b.Symbol, StringComparer.Ordinal)
            .ThenBy(b => b.Market)];
    }
}
