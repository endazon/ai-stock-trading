using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.TradeDecision.Domain;

// FR-04, FR-05, FR-10, #292, IADR-0119: LLM の判断（売買方向）と保有建玉から、発行すべき建玉効果を決める純関数。
//
// 従来は PositionEffect.Open がリテラル固定されていたため、LLM の Sell は「保有ロングの決済」ではなく
// **新規ショート建て**として扱われていた。結果、AI の売却が kill switch・pause・ロックアウト・同日再エントリー・
// 段階資金上限で止められ（FR-10 の「手仕舞いは止めない」と正反対）、保有ゼロなら裸の新規売りがブローカへ飛んでいた。
public static class PositionEffectResolver
{
    /// <summary>解決結果。<see cref="Effect"/> が null なら発注しない（見送り）。</summary>
    public readonly record struct PositionEffectDecision(PositionEffect? Effect, int CloseQuantity)
    {
        public bool IsClose => Effect == PositionEffect.Close;

        public bool IsSkipped => Effect is null;
    }

    /// <summary>
    /// <paramref name="signedHeldQuantity"/> は符号付き建玉（+ ロング / − ショート / 0 保有なし）。
    /// **null は「不明」**（照会不能）であり 0（保有なし）とは意味が異なる。
    /// </summary>
    public static PositionEffectDecision Resolve(TradeSide side, int? signedHeldQuantity)
    {
        // 建玉の反対売買は手仕舞い。数量は保有数（全量）＝ゼロを跨がないため IADR-0038 の分割は発生しない。
        if (signedHeldQuantity is > 0 && side == TradeSide.Sell)
            return new PositionEffectDecision(PositionEffect.Close, signedHeldQuantity.Value);

        if (signedHeldQuantity is < 0 && side == TradeSide.Buy)
            return new PositionEffectDecision(PositionEffect.Close, -signedHeldQuantity.Value);

        // 保有なし・不明での売りは新規ショート建てになる。現物のみ有効な現段階では成立せず、
        // ガード（ProductType/Market）は方向を見ないため素通りしてブローカへ飛ぶ。ADR-0003 に従い見送る。
        if (side == TradeSide.Sell && signedHeldQuantity is null or 0)
            return new PositionEffectDecision(Effect: null, CloseQuantity: 0);

        // それ以外（買い、あるいは同方向への建て増し）は従来どおり新規建て。
        return new PositionEffectDecision(PositionEffect.Open, CloseQuantity: 0);
    }
}
