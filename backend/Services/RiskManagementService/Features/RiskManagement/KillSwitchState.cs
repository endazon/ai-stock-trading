namespace RiskManagementService.Features.RiskManagement;

// FR-10, ADR-0003: 全停止スイッチ（kill switch）の状態。操作は利用者のみ（アクター・理由つき）。
// 新規建て（エントリー）のみを停止し、保有ポジションの手仕舞い（損切り）監視は維持する（フェイルセーフ）。
public record KillSwitchState(
    bool Engaged,
    string? Actor = null,
    string? Reason = null,
    DateTimeOffset? ChangedAt = null)
{
    /// <summary>初期状態（未起動）。</summary>
    public static readonly KillSwitchState Disengaged = new(false);

    /// <summary>
    /// 🔴 FR-10, FR-14, #1534, IADR-0620: 「見た状態」の版。発動・解除の要求は、照会で受け取ったこの値を前提条件として渡す
    /// （<see cref="KillSwitchRequest.ExpectedVersion"/>）。状態が書かれるたびに変わる（最後に変えた時刻の tick。未操作は "0"）。
    /// 照会の応答（JSON）に載る。比較は序数一致の文字列で行い、値の意味を呼び手に読ませない。
    /// </summary>
    public string Version => ChangedAt is { } at ? at.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0";
}
