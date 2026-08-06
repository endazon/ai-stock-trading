namespace AiStockTrading.MarketMonitor.Domain;

// FR-03, FR-13, SC-01 §2, #340, IADR-0155: 監視設定として設定できる値域。
//
// 計画（05_screens SC-01 §2）は変動閾値の検証を「**正値・下限/上限の範囲内**」とだけ定め、具体値を
// 与えていない。値域そのものは計画が要求しているため、**具体値は実装が決める**（リスク上限の
// `RiskLimitBounds`＝IADR-0151 決定2 と同じ扱い）。値の根拠は下記コメントに残し、計画へ環流する。
//
// **実効はサーバ側（ここ）である。** 画面（SC-01 §2）にも同じ表があるが、それは利用者へ即時に提示する
// ためであり、画面だけの関門は API 直叩きで消える（IADR-0141 決定1 と同じ判断）。
public static class MonitorSettingsBounds
{
    /// <summary>
    /// 変動閾値の下限（**この値は含まない**）。0 以下ではあらゆる変動が閾値超過となり、
    /// 監視が統制として働かない（イベント駆動サイクルが常時発火し LLM 費用が暴走する）。
    /// </summary>
    public const decimal MinMovementThresholdRatioExclusive = 0m;

    /// <summary>
    /// 変動閾値の上限（**この値を含む**。0.50 ＝ ±50%）。50% を超える閾値では 1 日で半値になるほどの
    /// 変動でしか発火せず、価格変動検知（FR-03）が事実上無効になる。既定は 0.03（±3%・FR-03 の確定値）。
    /// </summary>
    public const decimal MaxMovementThresholdRatio = 0.50m;

    /// <summary>
    /// クールダウンの上限（24 時間）。これを超えると同一銘柄が 1 営業日に 1 度も再判定されなくなり、
    /// 日中の再エントリー機会を構造的に失う。下限は 0（抑制なし）で、負値は不正である。
    /// </summary>
    public static readonly TimeSpan MaxCooldown = TimeSpan.FromHours(24);

    /// <summary>
    /// 変動閾値が値域に収まるか。収まらなければ利用者向けの説明を返す（収まれば <c>null</c>）。
    /// **黙って既定値へ丸めない**（丸めると「保存したつもりの値と違う値で監視が動く」状態を作る）。
    /// </summary>
    public static string? ValidateMovementThresholdRatio(decimal ratio) =>
        ratio <= MinMovementThresholdRatioExclusive || ratio > MaxMovementThresholdRatio
            ? $"変動閾値は {MinMovementThresholdRatioExclusive} 超 {MaxMovementThresholdRatio} 以下"
              + "（比率。0.03 ＝ ±3%）で指定してください。"
            : null;

    /// <summary>クールダウンが値域に収まるか。収まらなければ利用者向けの説明を返す。</summary>
    public static string? ValidateCooldown(TimeSpan cooldown) =>
        cooldown < TimeSpan.Zero || cooldown > MaxCooldown
            ? $"クールダウンは 0 以上 {MaxCooldown.TotalHours} 時間以下で指定してください。"
            : null;
}
