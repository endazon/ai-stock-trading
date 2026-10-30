namespace AiStockTrading.Shared.Kernel.Trading;

// 🔴 FR-10, FR-19, 計画 ADR-0077, #1486, #1519, IADR-0594 決定1, IADR-0606: 判断由来の**新規建て**の指値の未約定時の取消の既定（発注執行が読む）。
//
// 計画 ADR-0077（利用者裁定 2026-10-30・planning#852）: N は 15 分・回数の上限は 1 建玉 1 日 1 回（05_trading-assumptions §5 の新規建ての 2 行）。
// **既定は無効**のまま（本番の values.yaml も無効）。PoC の経路（values-local.yaml）だけが有効にする（決定 4）。
// N・回数は決済側の定数を**共用したまま**にする（IADR-0606。裁定の値が決済と同じであり、決済と同じ条件で実績を比べるため）。
// 🔴 共用でも、決済の値の変更が新規建てへ黙って及ばないよう、TradingDefaultsTests が新規建ての値を**字句で** 15・1 に固定する
// （決済の値を変えるとその試験が赤になり、新規建ての値をどうするかを同じ変更で決めさせる。ADR-0077 決定 2 の注意）。
public static class DecisionEntryUnfilledCancelDefaults
{
    /// <summary>既定は<b>無効</b>（計画 ADR-0077 決定 2・4。本番は無効のまま、PoC の経路だけが有効にする）。</summary>
    public const bool Enabled = false;

    /// <summary>N（分）。計画 ADR-0077 決定 2 の 15 分。決済側（§5・ADR-0067 決定2）の定数を共用する（IADR-0606）。</summary>
    public const int UnfilledMinutes = DecisionCloseUnfilledCancelDefaults.UnfilledMinutes;

    /// <summary>回数の上限（1 建玉・1 市場の現地取引日あたり）。計画 ADR-0077 決定 2 の 1 回。決済側の定数を共用する（IADR-0606）。</summary>
    public const int MaxCancelsPerPositionPerDay = DecisionCloseUnfilledCancelDefaults.MaxCancelsPerPositionPerDay;
}
