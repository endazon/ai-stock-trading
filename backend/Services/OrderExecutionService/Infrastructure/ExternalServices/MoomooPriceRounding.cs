using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Infrastructure.ExternalServices;

// FR-10, FR-12, ADR-0040 決定1（S3）, #844, IADR-0347: ブローカーへ送る価格を**市場の刻みへ丸める**。
//
// 🔴 稼働環境で実測（2026-09-18）: 代替の保護レグ（StopLimit）が
// `retType=-1 The precision of Price in Place Order does not meet the specification.` で拒否された。
// 指値を「発火価格 × (1 − 比率)」で作るため 332.35 × 0.99 = 329.0265 のように小数 4 桁になっていた。
// 事故当日は S0 の発火価格もエントリーの指値もたまたま 2 桁だったため、代替レグだけが露呈した
// ——**上流に丸めは無い**（下の #846 の段を見ること）。
//
// 丸めの向きは**保護が緩む側へ倒さない**。決済が売り（ロングの保護）なら切り下げ、買い戻し（ショートの保護）なら
// 切り上げ——どちらも「約定しやすい側」である。発火価格そのものは逆で、**早く発火する側**へ倒す。
//
// 🔴 **桁は「銘柄の基準価格」で一度だけ決め、指値にも同じ桁を使う**（`referencePrice`）。値ごとに $1 と比べると、
// 1 ドル近傍で指値 4 桁・発火価格 2 桁のように**桁が混ざる**。ブローカーの判定が銘柄価格で決まるなら同じ拒否が再発する。
//
// FR-05, FR-10, ADR-0016, #846, IADR-0210（2026-09-19 追記）: **S0 の発火価格とエントリーの指値もここを通る**
// （`MoomooBrokerAdapter.PlaceCoreAsync`）。#845 の時点では S3 だけが丸めを通っていたが、S0 は**実弾でも使う経路**で、
// 監査プローブが `Trigger=332.3512` / `entry Price=329.0265` を実測した。上流に丸めは無い。
//
// 🔴 **エントリーの指値だけは向きが逆である**（`RoundEntryLimit`）。保護レグの指値は「約定しないと保護にならない」ので
// 約定しやすい側へ倒すが、**エントリーは約定しなくても損をしない**。意図より悪い価格で建つと
// 「エントリー − 損切りライン」の実幅が広がり、サイジングが前提にした 1 株あたりリスクを超える。
//
// 残る制約:
// - 東証の呼値は価格帯で刻みが変わる（1 円・5 円・10 円…）。ここでは**小数桁だけ**を揃えるため、
//   高価格帯の日本株では刻みの倍数にならないことがある。S3 は SIMULATE 限定であり、実測でき次第見直す。
// - **日本株の低位株（数円）では、丸めたずらし幅が相対的に大きくなる**（30 円で 1 円＝3.3%）。1 円株では
//   指値が 0 になり、既存の発注前検証が送信を止める（fail-closed）。
// - **台帳には丸める前の値が残る**（`ExecutionRecord` / `ProtectiveStopOrder` は呼び出し側の値を保存する）。
//   「記録した価格」と「送った価格」の間に最大 1 刻みの差が残る（#846 の射程外）。
public static class MoomooPriceRounding
{
    /// <summary>1 ドル未満の米国株は小数 4 桁まで刻める（サブペニー）。それ以上は 2 桁。</summary>
    private const decimal SubDollarThreshold = 1m;

    /// <param name="referencePrice">
    /// 銘柄の基準価格（保護レグでは発火価格）。**丸める値そのものではなく、これで桁を決める**
    /// ——値ごとに判定すると 1 ドル近傍で桁が混ざる。
    /// </param>
    public static int DecimalsFor(Market market, decimal referencePrice) => market switch
    {
        // 日本株は円単位（小数を持たない）。
        Market.Japan => 0,
        _ => referencePrice < SubDollarThreshold ? 4 : 2,
    };

    public static decimal TickFor(Market market, decimal referencePrice)
    {
        var decimals = DecimalsFor(market, referencePrice);
        decimal tick = 1m;
        for (var i = 0; i < decimals; i++)
            tick /= 10m;
        return tick;
    }

    /// <summary>約定しやすい側へ丸める（売りは切り下げ・買いは切り上げ）。</summary>
    public static decimal RoundLimit(Market market, TradeSide side, decimal price, decimal referencePrice)
    {
        var decimals = DecimalsFor(market, referencePrice);
        return side == TradeSide.Sell ? Floor(price, decimals) : Ceiling(price, decimals);
    }

    /// <summary>
    /// #846: エントリーの指値は**不利にならない側**へ丸める（買いは切り下げ・売りは切り上げ）。
    /// <para>
    /// 🔴 <see cref="RoundLimit"/>（保護レグ用＝約定しやすい側）と**向きが逆**である。取り違えないよう別メソッドにしてある。
    /// 保護レグは約定しなければ保護にならないが、**エントリーは約定しなくても損をしない**（見送りは可逆）。
    /// 一方、意図より悪い価格で建つと「エントリー − 損切りライン」の実幅が広がり、サイジングが前提にした
    /// 1 株あたりリスク（FR-10）を超える——こちらは不可逆である。
    /// </para>
    /// <para>桁は**エントリーの指値そのもの**（＝銘柄の基準価格）で決める。他に基準となる価格を持たない。</para>
    /// </summary>
    public static decimal RoundEntryLimit(Market market, TradeSide side, decimal price)
    {
        var decimals = DecimalsFor(market, price);
        return side == TradeSide.Buy ? Floor(price, decimals) : Ceiling(price, decimals);
    }

    /// <summary>早く発火する側へ丸める（ロングの保護＝売りの発火は切り上げ・ショートの保護は切り下げ）。</summary>
    public static decimal RoundTrigger(Market market, TradeSide closeSide, decimal price)
    {
        // 発火価格そのものが基準価格である。
        var decimals = DecimalsFor(market, price);
        return closeSide == TradeSide.Sell ? Ceiling(price, decimals) : Floor(price, decimals);
    }

    /// <summary>
    /// トレール幅は**狭い側**（早く発火する側）へ丸める。
    /// 🔴 刻みに満たない幅は **0 のまま返す**——ここで 1 刻みを勝手に足すと、
    /// 「幅 0 は送らずに理由を残す」という発注前検証を無効化し、**決定が求めていない保護距離を捏造する**。
    /// </summary>
    public static decimal RoundTrail(Market market, decimal referencePrice, decimal trailValue) =>
        Floor(trailValue, DecimalsFor(market, referencePrice));

    /// <summary>
    /// 指値が発火価格と同値以上に寄ってしまった場合に、**1 刻みだけ不利側へ離す**。
    /// ずらし幅が刻みより小さいと丸めで同値になり、「保護レグを置いたのに約定しない」を作る。
    /// </summary>
    public static decimal EnsureBeyondTrigger(Market market, TradeSide side, decimal limitPrice, decimal triggerPrice)
    {
        var tick = TickFor(market, triggerPrice);
        if (side == TradeSide.Sell)
            return limitPrice >= triggerPrice ? triggerPrice - tick : limitPrice;

        return limitPrice <= triggerPrice ? triggerPrice + tick : limitPrice;
    }

    private static decimal Floor(decimal value, int decimals)
    {
        var factor = Factor(decimals);
        return Math.Floor(value * factor) / factor;
    }

    private static decimal Ceiling(decimal value, int decimals)
    {
        var factor = Factor(decimals);
        return Math.Ceiling(value * factor) / factor;
    }

    private static decimal Factor(int decimals)
    {
        decimal factor = 1m;
        for (var i = 0; i < decimals; i++)
            factor *= 10m;
        return factor;
    }
}
