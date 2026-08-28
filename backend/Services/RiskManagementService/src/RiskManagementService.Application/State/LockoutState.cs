namespace RiskManagementService.Application.State;

// FR-10, IADR-0008: 日次損失上限到達後のロックアウト状態。当日全停止し、翌営業日（ReleaseOn）まで新規発注を止める
// （05_trading-assumptions §5「デイリーストップ」）。ステートレスな RiskEvaluator では「翌営業日まで」を表現できない
// ため、この状態をホスト（アプリケーション層）が保持する。含み損が回復しても当日中はロックを維持する。
public record LockoutState(
    DateOnly ReleaseOn,
    string Reason,
    DateTimeOffset EngagedAt)
{
    /// <summary>指定日（当日）時点でロックアウトが有効か。解除日（ReleaseOn）以降は無効。</summary>
    public bool IsActiveOn(DateOnly today) => today < ReleaseOn;
}
