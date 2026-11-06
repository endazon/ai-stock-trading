namespace RiskManagementService.Features.RiskManagement;

// FR-10, ADR-0003: 全停止スイッチ（kill switch）の状態。操作は利用者のみ（アクター・理由つき）。
// 新規建て（エントリー）のみを停止し、保有ポジションの手仕舞い（損切り）監視は維持する（フェイルセーフ）。
public record KillSwitchState(
    bool Engaged,
    string? Actor = null,
    string? Reason = null,
    DateTimeOffset? ChangedAt = null,
    [property: System.Text.Json.Serialization.JsonIgnore] long Revision = 0)
{
    /// <summary>初期状態（未起動）。</summary>
    public static readonly KillSwitchState Disengaged = new(false);

    /// <summary>
    /// 🔴 FR-10, FR-14, #1534, IADR-0620: 「見た状態」の版。発動・解除の要求は、照会で受け取ったこの値を前提条件として渡す
    /// （<see cref="KillSwitchRequest.ExpectedVersion"/>）。状態が書かれるたびに 1 つ進む単調増加の値（<see cref="Revision"/>。未操作は "0"）。
    /// 時刻の tick にしない —— 時計の戻り・同じ tick の 2 回の書き込みで同じ版が再び現れ（ABA）、古い状態を見た操作が通るため。
    /// 照会の応答（JSON）に載る。比較は序数一致の文字列で行い、値の意味を呼び手に読ませない。
    /// </summary>
    public string Version => Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
