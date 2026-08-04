namespace AiStockTrading.RiskManagement.Application.State;

// FR-10, ADR-0009: 取引の一時停止（pause）の状態。利用者が手動で発動し（アクター・理由つき）、
// 期限を持たず `/resume` まで継続する軽い統制。kill switch と同型で、新規建て（エントリー）のみを停止し、
// 保有ポジションの手仕舞い（損切り）監視は維持する（フェイルセーフ）。日次損失ロックアウト（IADR-0008）とは別状態。
public record PauseState(
    bool Paused,
    string? Actor = null,
    string? Reason = null,
    DateTimeOffset? ChangedAt = null)
{
    /// <summary>初期状態（非停止）。</summary>
    public static readonly PauseState NotPaused = new(false);
}
