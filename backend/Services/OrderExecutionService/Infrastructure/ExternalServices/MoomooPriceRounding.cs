using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Infrastructure.ExternalServices;

// FR-10, FR-12, ADR-0040 決定1（S3）, #844, IADR-0347: ブローカーへ送る価格を**市場の刻みへ丸める**。
//
// 🔴 稼働環境で実測（2026-09-18）: 代替の保護レグ（StopLimit）が
// `retType=-1 The precision of Price in Place Order does not meet the specification.` で拒否された。
// 指値を「発火価格 × (1 − 比率)」で作るため 332.35 × 0.99 = 329.0265 のように小数 4 桁になっていた。
// エントリーの指値は上流で刻みに収まっているため、**代替レグだけがこれを踏む**。
//
// 丸めの向きは**保護が緩む側へ倒さない**。決済が売り（ロングの保護）なら切り下げ、買い戻し（ショートの保護）なら
// 切り上げ——どちらも「約定しやすい側」である。発火価格そのものは逆で、**早く発火する側**へ倒す。
//
// 残る制約: 東証の呼値は価格帯で刻みが変わる（1 円・5 円・10 円…）。ここでは**小数桁だけ**を揃えるため、
// 高価格帯の日本株では刻みの倍数にならないことがある。S3 は SIMULATE 限定であり、実測でき次第見直す。
public static class MoomooPriceRounding
{
    /// <summary>1 ドル未満の米国株は小数 4 桁まで刻める（サブペニー）。それ以上は 2 桁。</summary>
    private const decimal SubDollarThreshold = 1m;

    public static int DecimalsFor(Market market, decimal price) => market switch
    {
        // 日本株は円単位（小数を持たない）。
        Market.Japan => 0,
        _ => price < SubDollarThreshold ? 4 : 2,
    };

    public static decimal TickFor(Market market, decimal price)
    {
        var decimals = DecimalsFor(market, price);
        decimal tick = 1m;
        for (var i = 0; i < decimals; i++)
            tick /= 10m;
        return tick;
    }

    /// <summary>約定しやすい側へ丸める（売りは切り下げ・買いは切り上げ）。</summary>
    public static decimal RoundLimit(Market market, TradeSide side, decimal price)
    {
        var decimals = DecimalsFor(market, price);
        return side == TradeSide.Sell ? Floor(price, decimals) : Ceiling(price, decimals);
    }

    /// <summary>早く発火する側へ丸める（ロングの保護＝売りの発火は切り上げ・ショートの保護は切り下げ）。</summary>
    public static decimal RoundTrigger(Market market, TradeSide closeSide, decimal price)
    {
        var decimals = DecimalsFor(market, price);
        return closeSide == TradeSide.Sell ? Ceiling(price, decimals) : Floor(price, decimals);
    }

    /// <summary>
    /// トレール幅は**狭い側**（早く発火する側）へ丸める。
    /// 🔴 刻みに満たない幅は **0 のまま返す**——ここで 1 刻みを勝手に足すと、
    /// 「幅 0 は送らずに理由を残す」という発注前検証を無効化し、**決定が求めていない保護距離を捏造する**。
    /// </summary>
    public static decimal RoundTrail(Market market, decimal price, decimal trailValue) =>
        Floor(trailValue, DecimalsFor(market, price));

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
