namespace RiskManagementService.Domain.Manipulation;

// FR-19, IADR-0040: 相場操縦とみなされ得る発注パターンのシグナル種別（計画書 06_daytrading-review §2.3 の禁止対象）。
public enum ManipulationSignal
{
    /// <summary>過剰な取消: 約定なし取消が発注に占める比率が過大。</summary>
    ExcessiveCancellations,

    /// <summary>過剰な訂正: 1 発注あたりの訂正反復が過大。</summary>
    ExcessiveAmendments,

    /// <summary>見せ玉（約定意思のない発注）: 低約定率かつ短命取消の反復。</summary>
    NoExecutionIntent,

    /// <summary>板演出（レイヤリング）: 同一方向の約定なし取消注文が同時に多段生存。</summary>
    Layering,
}
