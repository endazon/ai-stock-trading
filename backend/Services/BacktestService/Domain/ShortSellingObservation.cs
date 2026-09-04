using AiStockTrading.Shared.Contracts.Trading;

namespace BacktestService.Domain;

// FR-15, FR-20, ADR-0016 決定14, #388, IADR-0304: **検証した走行が空売りを含んでいたかを観測する純関数。**
//
// 計画（ADR-0016 決定14）は「**空売りを含む戦略で** Stage 0 の 7 条件を再度満たす」ことを Stage 3 の
// 空売り実弾解禁の前提条件とするが、**「含む」の判定方法を定めていない**（申告か観測か）。
// 本実装は**観測**を採る——申告は、渡し違えれば「一度も空売りしていない戦略の合格」で実弾の空売りが
// 解禁され得るためである（#388 が最重要とした否定形を、呼び出し元の正直さだけで守ることになる）。
//
// **観測の定義**: 約定を時系列に畳んだとき、いずれかの (銘柄, 市場) で**累計建玉が一度でも負になった**こと。
// バックテストは建玉ゼロから始まるため（BacktestSimulator）、累計が負＝売り建てである。
public static class ShortSellingObservation
{
    /// <summary>
    /// 走行の約定列から、空売り建玉を持った時点があったかを観測する。
    /// <para>
    /// **未約定は「含まない」へ倒す。** 空売り注文を出したが約定しなかった走行は、借株料も
    /// ドローダウンも検証していない——決定14 が求める「空売りを含む戦略での再充足」を満たしていない。
    /// 保守的な側であり、計画が判定方法を定めていない以上こちらを採る（IADR-0304 決定2）。
    /// </para>
    /// </summary>
    /// <param name="fills">走行の約定列（<c>BacktestRun.Fills</c>）。<c>null</c>・空はいずれも <c>false</c>。</param>
    public static bool Includes(IReadOnlyList<BacktestFill>? fills)
    {
        if (fills is null || fills.Count == 0)
        {
            return false;
        }

        var net = new Dictionary<(string Symbol, Market Market), int>();
        foreach (var fill in fills)
        {
            var key = (fill.Symbol, fill.Market);
            var quantity = net.TryGetValue(key, out var running) ? running : 0;
            quantity += fill.SignedQuantity;
            net[key] = quantity;

            // 一度でも売り建てになったら、以降どう畳まれても「空売りを含む」である。
            if (quantity < 0)
            {
                return true;
            }
        }

        return false;
    }
}
