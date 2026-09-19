using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Domain;

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
    /// <param name="requireKnownHoldingForOpen">
    /// FR-04, FR-10, ADR-0003, #865, IADR-0358: true なら**保有が不明（null）のときに新規建て（Open）を見送る**。
    /// 呼び出し側は保有状況の照会先が実結線されているか（<c>IHeldPositionProvider.IsEnabled</c>）を渡す。
    ///
    /// 🔴 **止めるのは Open だけである。** 決済（Close）の分岐はこの引数より前にあり、保有が判っている限り
    /// 常に通る（FR-10「手仕舞いは止めない」）。不明のときは決済の分岐に入りようがないため、
    /// この引数が出口を塞ぐことはない。
    ///
    /// 既定は false ＝従来どおり（IADR-0119 決定2）。未結線（NoOp＝常に不明）の既定構成の挙動を変えないため、
    /// **不在が統制の有効を意味する形にしない**（不明を一律に止めると既定構成で新規建てが一切できなくなる）。
    /// </param>
    public static PositionEffectDecision Resolve(
        TradeSide side, int? signedHeldQuantity, bool requireKnownHoldingForOpen = false)
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

        // FR-04, FR-10, ADR-0003, #865, IADR-0358: 実結線のもとでの「不明」は**照会したが答えが得られなかった**である。
        // その状態の新規建ては、保有を知らないまま買い増しを重ねる経路そのものであり（#854 の実測）、
        // プロンプトの「不明なら Hold」は LLM への依頼でしかない（IADR-0351 決定2・残る制約）。ここで止める。
        if (requireKnownHoldingForOpen && signedHeldQuantity is null)
            return new PositionEffectDecision(Effect: null, CloseQuantity: 0);

        // それ以外（買い、あるいは同方向への建て増し）は従来どおり新規建て。
        return new PositionEffectDecision(PositionEffect.Open, CloseQuantity: 0);
    }
}
