using AiStockTrading.Shared.Contracts.Trading;

namespace ReportService.Domain;

// FR-06, FR-16, #892, IADR-0033, IADR-0301, IADR-0381:
// **期間で切った在庫**へ 1 約定を適用する純関数（報告書サービスの畳み込みの単一情報源）。
//
// 🔴 **報告書の在庫は「その期間の約定だけ」から畳まれる**（期間の切り出しは権威源側の `PeriodFillQuery`）。
// したがって**期間より前に建てた建玉は報告書の在庫に存在しない**。それを当期に決済した約定を
// <see cref="SignedInventory.Apply"/> へ素で通すと、在庫 0 への反対売買が**新規建て**として畳まれ、
// 平均取得単価＝決済価格の**幻のショート**が開く。幻のショートは
//   - 現在値を引かれて**実在しない建玉の評価損益**になる（日報 §1）
//   - 続く決済を「決済」ではなく「新規ショート」に変え、**実現損益・決済件数・勝率**を落とす
//
// 🔴 **取り込み（PeriodDriftAdoption）のクランプをそのまま当てることはできない。** 取り込みは
// 「在庫を減らすだけ」の操作であり建てる正当な用途が無いが（IADR-0360 決定 4）、**約定は建てる操作でもある**
// ——「在庫 0 の売り」を一律に無視すると、当期に始めた**正当な新規ショート**まで消える。
//
// 🔴 **見分けるのは台帳が既に持っている軸** <see cref="PositionEffect"/> である（新しい入力は要らない）。
// `PositionEffect` は取引判断が**判断時点の実保有数量**から解決し（`PositionEffectResolver`）、
// 台帳（`LedgerFill.PositionEffect`）へ記録され、`GET /risk-controls/fills` から素通しで届く。
//   - `Open`  ＝新規建て。**当期に始まった建玉**であり、在庫 0 でも建ててよい。
//   - `Close` ＝手仕舞い。**台帳の建玉を減らす約定**であり、期間の在庫で賄えない分は
//     **期間より前に建てた建玉**である ⇒ 取得原価が当期間に無く、**実現損益を算定できない**。
//
// 🔴 **クランプは「決済が無かったことにする」のではない。** 約定件数・費用（約定ごとに掛かり取得原価を
// 要しない）は従来どおり計上する。落とすのは**取得原価を要する値だけ**であり、落としたことは
// <see cref="PeriodInventoryFill.UnvaluedQuantity"/> で呼び出し側へ返す（**黙って落とさない**）。
public static class PeriodInventory
{
    /// <summary>
    /// この約定のうち<b>期間の在庫で賄えない数量</b>（取得原価が当期間に無く評価できない数量。0 なら全量が評価できる）。
    /// <para>
    /// <paramref name="signedQuantity"/> は符号付き約定数量（買いが +・売りが −。
    /// <see cref="SignedInventory"/> と同じ規則）。
    /// </para>
    /// <para>
    /// 🔴 <b><see cref="PositionEffect.Open"/> は常に 0 を返す。</b> 新規建て・建て増し・反転は
    /// 当期に始まった建玉であり、幻ではない（ここで巻き添えにすると正当な新規ショートが消える）。
    /// </para>
    /// </summary>
    public static int UnvaluedQuantity(int currentQuantity, PositionEffect effect, int signedQuantity)
    {
        if (effect != PositionEffect.Close)
            return 0;

        var quantity = Math.Abs(signedQuantity);

        // 在庫が無い／同方向＝減らす対象が期間の在庫に無い＝全量が期間より前に建てた建玉である。
        if (currentQuantity == 0 || Math.Sign(currentQuantity) == Math.Sign(signedQuantity))
            return quantity;

        // 反対方向＝賄える分（在庫の絶対値）を超えた分だけが期間より前の建玉である。
        return quantity - Math.Min(Math.Abs(currentQuantity), quantity);
    }

    /// <summary>
    /// 期間で切った在庫へ 1 約定を適用する。
    /// <para>
    /// 賄える分は <see cref="SignedInventory.Apply"/>（IADR-0033・平均取得単価法）が畳み、
    /// 賄えない分は<b>建てない（0 でクランプする）</b>。賄えない分の実現損益は<b>算定しない</b>
    /// ——取得原価が当期間の約定に含まれていないため、値を作れば捏造になる。
    /// </para>
    /// </summary>
    public static PeriodInventoryFill Apply(
        InventoryLot lot, PositionEffect effect, int signedQuantity, decimal price)
    {
        var unvalued = UnvaluedQuantity(lot.Quantity, effect, signedQuantity);
        if (unvalued == 0)
        {
            var whole = SignedInventory.Apply(lot, signedQuantity, price);
            return new PeriodInventoryFill(whole.Lot, whole.RealizedPnl, whole.Reduced, 0);
        }

        var covered = Math.Abs(signedQuantity) - unvalued;
        if (covered == 0)
        {
            // 賄える分が無い＝在庫は 1 株も減らせない。**建てない・反転しない**（在庫をそのまま返す）。
            return new PeriodInventoryFill(lot, 0m, Reduced: false, unvalued);
        }

        // 一部だけ賄える。賄えた分を決済し（実現損益はその分だけ）、残りは建てない。
        var applied = SignedInventory.Apply(lot, Math.Sign(signedQuantity) * covered, price);
        return new PeriodInventoryFill(applied.Lot, applied.RealizedPnl, applied.Reduced, unvalued);
    }
}

/// <summary>
/// FR-06, FR-16, #892, IADR-0381: 期間で切った在庫へ 1 約定を適用した結果。
/// </summary>
/// <param name="Lot">適用後の在庫（🔴 <b>幻の建玉を含まない</b>）。</param>
/// <param name="RealizedPnl">
/// <b>賄えた分だけ</b>の実現損益（税引前・費用前）。<see cref="UnvaluedQuantity"/> のぶんは含まない。
/// </param>
/// <param name="Reduced">期間の在庫が実際に減った（＝算定できる決済が発生した）か。</param>
/// <param name="UnvaluedQuantity">
/// 取得原価が当期間に無く<b>実現損益を算定できなかった数量</b>（0 ＝全量を算定できた）。
/// 🔴 <b>呼び出し側はこれを黙って捨ててはならない</b>——捨てると「決済が無かった」「損益 0 だった」と読める。
/// </param>
public readonly record struct PeriodInventoryFill(
    InventoryLot Lot,
    decimal RealizedPnl,
    bool Reduced,
    int UnvaluedQuantity)
{
    /// <summary>取得原価が当期間に無く、この約定の実現損益を算定できなかったか。</summary>
    public bool Unvalued => UnvaluedQuantity > 0;
}
