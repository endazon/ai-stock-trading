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
    /// FR-06, FR-11, #870, #859, IADR-0360 決定 4（2026-09-19 改定）:
    /// 取り込みを適用した後の<b>符号付き在庫数量</b>（純関数）。
    /// <para>
    /// 🔴 <b>取り込みは在庫を「減らす」ことしかできない。建てない・反転しない。</b>
    /// <list type="bullet">
    /// <item>在庫 0 ／ 同方向 ⇒ <b>何も起きない</b>（現在の数量をそのまま返す）。</item>
    /// <item>反対方向 ⇒ 減らす。<b>在庫を超える分は 0 でクランプする</b>（余りを新しい建玉にしない）。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 🔴 <b><see cref="SignedInventory.Apply"/> をそのまま使ってはならない。</b> 同関数は在庫 0 を<b>新規建て</b>として扱い、
    /// 反転では余りを<b>新しい建玉</b>にする。報告書側の在庫は<b>その期間の約定だけ</b>から畳まれるため
    /// （期間の切り出しは <c>PeriodFillQuery</c>）、<b>期間より前に建てた建玉は報告書の在庫に存在しない</b> ——
    /// それを手動で売った取り込みを素の <c>Apply</c> へ通すと<b>平均取得単価 0 の幻のショート</b>が開く。
    /// 幻のショートは現在値を引かれて<b>評価損益として日報 §1 に出る</b>うえ、後続のシステムの売り約定を
    /// 「決済」ではなく「新規ショート」に変えて<b>実現損益・決済件数・勝率まで汚す</b>
    ///（#859 が止めようとした「実在しない建玉の評価損益」を符号違いで再導入する）。
    /// </para>
    /// <para>
    /// 🔴 <b>クランプは「取り込みを無かったことにする」のではない。</b> 報告書が知らない在庫は報告書が評価もしていない
    /// ——減らす対象がそもそも無いだけである。<b>取り込みの事実は日報 §2-b が別掲する</b>（それが §2-b の目的である）。
    /// </para>
    /// </summary>
    public static int ReducedQuantity(int currentQuantity, int signedQuantity)
    {
        // 在庫が無い／同方向＝減らす対象が無い。**建てない。**
        if (currentQuantity == 0 || Math.Sign(currentQuantity) == Math.Sign(signedQuantity))
            return currentQuantity;

        var remaining = currentQuantity + signedQuantity;

        // 符号が変わる（＝在庫を超えて減らした）なら 0 でクランプする。**反転させない。**
        return Math.Sign(remaining) == Math.Sign(currentQuantity) ? remaining : 0;
    }

    /// <summary>
    /// 在庫へ<b>数量だけ</b>適用する。
    /// <para>
    /// 規則は <see cref="ReducedQuantity"/> ——<b>減らす方向にしか効かず、建てない・反転しない。</b>
    /// <b>平均取得単価は動かさない</b>（リスク管理の <c>PortfolioProjection.ApplyToLot</c> が
    /// 「その時点の平均取得単価で畳む」＝実現損益 0 を丸め誤差なしに保証するのと同じ結果になる。
    /// 🔴 <b>ただし在庫 0・在庫超過の扱いは同じではない</b> —— 理由は <see cref="ReducedQuantity"/> を参照）。
    /// 在庫が 0 になったときだけ取得単価も落とす（<see cref="SignedInventory"/> の全決済と同じ形）。
    /// </para>
    /// <para>🔴 <b>戻り値は在庫だけである。</b> 実現損益・決済件数・費用を呼び出し側へ返さない（算入させない）。</para>
    /// </summary>
    public InventoryLot ApplyTo(InventoryLot lot)
    {
        var quantity = ReducedQuantity(lot.Quantity, SignedQuantity);
        return quantity == 0 ? default : lot with { Quantity = quantity };
    }
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
