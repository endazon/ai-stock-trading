using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace ReportService.Domain;

// FR-06, FR-11, FR-16, 04_report-templates 日報 §2-b, ADR-0041 決定 1, #870, #859, IADR-0350, IADR-0360:
// 期間の**乖離の取り込み** 1 件（利用者が証券会社のアプリから直接売買した結果を、取引台帳へ取り込んだ記録）。
//
// 🔴 **約定（PeriodTradeFill）とは別の型である。** 混ぜない。
// システム外の売買は**約定価格が分からない**ため、この行は**数量だけ**を運ぶ。実現損益は **`不明`**（0 ではない）。
// 別の型で運ぶことにより、実現損益・決済件数・勝率・費用の概算・三者比較のどれへも
// **渡すことが型として不可能**になる（IADR-0360 決定 2。除外を書き落とす余地を消す）。
public sealed record PeriodDriftAdoption(
    Guid AdoptionId,
    string Symbol,
    Market Market,
    /// <summary>台帳へ適用した減少の方向（ロングの減少は Sell・ショートの減少は Buy）。</summary>
    TradeSide Side,
    /// <summary>減らした数量（&gt; 0）。</summary>
    int Quantity,
    /// <summary>取り込み前の台帳の数量（符号付き）。日報 §2-b の「取り込み前の数量」列。</summary>
    int LedgerQuantityBefore,
    /// <summary>観測されたブローカーの数量（符号付き）。日報 §2-b の「観測された数量」列。</summary>
    int BrokerQuantity,
    /// <summary>ブローカー建玉を観測した時刻。日報 §2-b の「観測時刻」列。</summary>
    DateTimeOffset ObservedAt,
    /// <summary>取り込みを承認した操作者。日報 §2-b の「操作者」列。</summary>
    string Actor,
    /// <summary>取り込みの理由（利用者が入力した一文）。日報 §2-b の「理由」列。</summary>
    string Reason,
    /// <summary>取り込み日時（台帳へ効いた時刻）。日報 §2-b の「取り込み日時」列であり、在庫を畳む順序の鍵でもある。</summary>
    DateTimeOffset AdoptedAt)
{
    /// <summary>在庫へ適用する符号付き数量（買いが +・売りが −。<see cref="SignedInventory"/> と同じ規則）。</summary>
    public int SignedQuantity => Side == TradeSide.Buy ? Quantity : -Quantity;

    /// <summary>
    /// 在庫へ<b>数量だけ</b>適用する（リスク管理の <c>PortfolioProjection.ApplyToLot</c> と<b>同じ規則</b>）。
    /// <para>
    /// 🔴 <b>決済単価に「その時点の平均取得単価」そのものを渡す。</b> <see cref="SignedInventory.Apply"/> の実現損益は
    /// (決済単価 − 取得単価) × 数量 なので、これで<b>丸め誤差なしに 0</b> になる。取得単価は不変のため、
    /// 部分的な取り込みの後も残りの建玉の評価は変わらない。
    /// </para>
    /// <para>🔴 <b>戻り値は在庫だけである。</b> 実現損益・決済件数・費用を呼び出し側へ返さない（算入させない）。</para>
    /// </summary>
    public InventoryLot ApplyTo(InventoryLot lot) =>
        SignedInventory.Apply(lot, SignedQuantity, lot.AverageCost).Lot;
}

// FR-06, FR-16, #870, #859, IADR-0360 決定 4: 約定と取り込みを**時系列に 1 本へ並べる**純関数。
//
// 報告書の畳み込み（PnlAggregator / FillPnlAttributionBuilder / TradeHistoryViewBuilder / FxTranslationBuilder /
// 現在値の解決）は**すべて同じ順序**で在庫を畳まなければならない —— 順序が違うと内訳の合計が §1 サマリと
// 一致しなくなり、しかも**各集計は自分の中では整合しているため全テストが緑のままそうなる**（IADR-0301 の明文）。
// 順序の定義を 1 箇所に置くのはそのためである。
public static class PeriodLedgerTimeline
{
    /// <summary>
    /// 約定（<paramref name="fills"/>）と取り込み（<paramref name="adoptions"/>）を時刻の昇順で 1 本に並べる。
    /// <para>
    /// 同時刻は<b>約定を先に</b>畳む —— 権威源（リスク管理の台帳の読み口）が取り込み行を約定の後ろへ合流させており、
    /// その安定並べ替えと同じ順序になる。<paramref name="adoptions"/> が <c>null</c>（照会できていない）なら
    /// <b>約定だけ</b>を並べる（取り込みを 0 件と騙らない）。
    /// </para>
    /// </summary>
    public static IReadOnlyList<PeriodLedgerEntry> Merge(
        IReadOnlyList<PeriodTradeFill> fills,
        IReadOnlyList<PeriodDriftAdoption>? adoptions)
    {
        ArgumentNullException.ThrowIfNull(fills);

        var entries = new List<PeriodLedgerEntry>(fills.Count + (adoptions?.Count ?? 0));
        entries.AddRange(fills.Select(f => new PeriodLedgerEntry(f, null)));
        if (adoptions is not null)
            entries.AddRange(adoptions.Select(a => new PeriodLedgerEntry(null, a)));

        // OrderBy は安定であり、同時刻では上で先に積んだ約定が前へ来る。
        return [.. entries.OrderBy(e => e.At)];
    }
}

/// <summary>
/// 時系列に並べた台帳の 1 件。<see cref="Fill"/> か <see cref="Adoption"/> の<b>どちらか一方</b>だけが非 <c>null</c>。
/// </summary>
public readonly record struct PeriodLedgerEntry(PeriodTradeFill? Fill, PeriodDriftAdoption? Adoption)
{
    /// <summary>畳み込みの順序に使う時刻（約定時刻／取り込み日時）。</summary>
    public DateTimeOffset At => Fill?.ExecutedAt ?? Adoption!.AdoptedAt;
}
