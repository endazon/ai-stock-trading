namespace AiStockTrading.Shared.Kernel.Trading;

// 🔴 FR-10, FR-19, ADR-0067 決定2・3, #1339, IADR-0539: 判断由来の決済の指値の未約定時の取消（05_trading-assumptions §5 の 2 行）と、
// その N の下端を決める相場操縦の検知の窓の**単一情報源**。
//
// 発注執行（取消をする側）とリスク管理（検知をする側）は互いを参照できない。両者が同じ定数を読むことで、
// 「有効時の N は検知の窓より長い」（ADR-0067 決定 2）を構成の検証で強制できる。**窓を変えると N の下端も一緒に動く。**
// 既定値はリスク管理の TradingDefaults が公開し、TradingDefaultsTests が計画の値で固定する。
public static class DecisionCloseUnfilledCancelDefaults
{
    /// <summary>§5: 既定は<b>無効</b>（ADR-0067 決定 2。有効化は #1214 の実弾確認の後・決定 6）。</summary>
    public const bool Enabled = false;

    /// <summary>§5: N の既定（分）。IADR-0536 の未約定の決済の WRN の閾値と揃えた暫定値。</summary>
    public const int UnfilledMinutes = 15;

    /// <summary>§5: 取消の回数の上限の既定（1 建玉あたり・市場の現地取引日あたり）。</summary>
    public const int MaxCancelsPerPositionPerDay = 1;

    /// <summary>IADR-0539: N の上端（分）。米国の通常取引時間（6.5 時間）。これより長い N は DAY の失効より後になり意味を持たない。</summary>
    public const int MaxUnfilledMinutes = 390;

    /// <summary>IADR-0539: 回数の上限の下端（§5「正の整数」）。</summary>
    public const int MinCancelsPerPositionPerDayBound = 1;

    /// <summary>IADR-0539: 回数の上限の上端。取消の反復を構造的に起こさない（ADR-0067 決定 3）ための小さな値に留める。</summary>
    public const int MaxCancelsPerPositionPerDayBound = 3;

    /// <summary>
    /// FR-19, IADR-0040: 相場操縦の検知の窓（分）。リスク管理の <c>TradingDefaults.CreateManipulationDetectionSettings</c> が
    /// この値から窓を作る。🔴 <b>有効時の N はこれより長くなければならない</b>（窓は発注時刻で切るため、発注から N 分たった取消は窓の外にある）。
    /// </summary>
    public const int ManipulationLookbackWindowMinutes = 5;
}
