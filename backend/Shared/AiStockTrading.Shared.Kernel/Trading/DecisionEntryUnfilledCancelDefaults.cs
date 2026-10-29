namespace AiStockTrading.Shared.Kernel.Trading;

// 🔴 FR-10, FR-19, #1486, IADR-0594 決定1: 判断由来の**新規建て**の指値の未約定時の取消の既定（発注執行が読む）。
//
// 🔴 **計画の裁定待ち**（planning#852）。計画 05_trading-assumptions §5 にある取消は判断由来の**決済**だけ（ADR-0067 の射程）で、
// 新規建てには規則も値も無い。仕組みだけを置き、**既定は無効**にする。有効化は裁定の後とする（本番の values.yaml・PoC の values-local.yaml とも有効にしない）。
// 有効にしたときの N・回数の上限は、新しい値を作らず決済側の定数を共用する（**暫定の初期値**。裁定で値が決まれば、ここを決まった値へ改める）。
// 既定値はリスク管理の TradingDefaults が公開し、TradingDefaultsTests が固定する。
public static class DecisionEntryUnfilledCancelDefaults
{
    /// <summary>既定は<b>無効</b>（裁定待ち。planning#852）。</summary>
    public const bool Enabled = false;

    /// <summary>N の暫定の初期値（分）。決済側（§5・ADR-0067 決定2）の値を共用する。</summary>
    public const int UnfilledMinutes = DecisionCloseUnfilledCancelDefaults.UnfilledMinutes;

    /// <summary>回数の上限の暫定の初期値（1 建玉・1 市場の現地取引日あたり）。決済側の値を共用する。</summary>
    public const int MaxCancelsPerPositionPerDay = DecisionCloseUnfilledCancelDefaults.MaxCancelsPerPositionPerDay;
}
