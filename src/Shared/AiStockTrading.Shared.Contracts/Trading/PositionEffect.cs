namespace AiStockTrading.Shared.Contracts.Trading;

// FR-10, FR-19, ADR-0007, IADR-0004: 建玉効果（新規建て/手仕舞い）。売買方向（TradeSide）と直交する。
// エントリー専用のリスク統制（kill switch・各金額上限・段階資金上限・同日再エントリー等）は
// Open（新規建て）にのみ適用し、Close（手仕舞い）はフェイルセーフによりブロックしない。
public enum PositionEffect
{
    /// <summary>新規建て（エントリー）。ロング買い建て・ショート売り建ての両方。</summary>
    Open,

    /// <summary>手仕舞い（決済）。ロング売り決済・ショート買い戻しの両方。</summary>
    Close,
}
