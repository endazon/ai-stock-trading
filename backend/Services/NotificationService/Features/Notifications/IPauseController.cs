namespace NotificationService.Features.Notifications;

// FR-14, UC-06, UC-07, ADR-0009, IADR-0075: リスク管理（#12）の一時停止/再開・状態照会エンドポイントの抽象。
// 通知サービスは pause の状態を持たず、既存の Risk エンドポイントを呼ぶだけ（権威は Risk 側）。
//
// pause/resume/status はいずれも OwnerOnly（trading-owner）のため、実装は Bot 専用の owner マップ機密クライアントの
// client_credentials トークンを付与する（trading-service トークンでは 403）。kill switch（IKillSwitchController）と同型。
public interface IPauseController
{
    // 新規建ての一時停止。既に停止中なら Risk が現状態を返すのみ（冪等）。
    Task<PauseResult> PauseAsync(string reason, CancellationToken cancellationToken = default);

    // 一時停止の解除。pause のみ解除する（kill switch・日次損失ロックアウトは解除しない）。
    Task<PauseResult> ResumeAsync(string reason, CancellationToken cancellationToken = default);

    // 稼働状態の参照（表示専用）。3 統制の状態・優先順位・段階・当日損益・上限使用率・ポジションを取得する。
    Task<RiskStatusResult> GetStatusAsync(CancellationToken cancellationToken = default);
}

// FR-14: 一時停止/再開操作の結果。Paused は操作後の状態。失敗時は Succeeded=false とし、呼び出し側は
// 利用者へ失敗を明示する（成功したように見せない）。
public sealed record PauseResult(bool Succeeded, bool Paused, string Message);

// FR-14, UC-07: 稼働状態照会の結果。Message は利用者へ表示する整形済みテキスト（表示専用）。
public sealed record RiskStatusResult(bool Succeeded, string Message);
