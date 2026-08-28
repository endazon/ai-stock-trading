namespace NotificationService.Application.Ports;

// FR-14, UC-06, IADR-0062 決定4: リスク管理（#12）の kill switch HTTP エンドポイントの抽象。
// 通知サービスは kill switch の状態を持たず、**既存エンドポイントを呼ぶだけ**（権威は Risk 側）。
//
// 当該エンドポイントは OwnerOnly（trading-owner）のため、実装は Bot 専用の owner マップ機密クライアントの
// client_credentials トークンを付与する（trading-service トークンでは 403）。
public interface IKillSwitchController
{
    // 全停止の起動。既に起動中なら Risk が現状態を返すのみ（冪等・詳細設計07）。
    Task<KillSwitchResult> EngageAsync(string reason, CancellationToken cancellationToken = default);

    // 全停止の解除。
    Task<KillSwitchResult> DisengageAsync(string reason, CancellationToken cancellationToken = default);
}

// FR-14: kill switch 操作の結果。Engaged は操作後の状態。失敗時は Succeeded=false とし、
// 呼び出し側は利用者へ失敗を明示する（成功したように見せない）。
public sealed record KillSwitchResult(bool Succeeded, bool Engaged, string Message);
