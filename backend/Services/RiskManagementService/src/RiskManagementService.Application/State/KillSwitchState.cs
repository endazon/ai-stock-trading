namespace RiskManagementService.Application.State;

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
}
