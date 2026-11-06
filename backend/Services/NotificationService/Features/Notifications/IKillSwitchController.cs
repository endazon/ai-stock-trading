namespace NotificationService.Features.Notifications;

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

    // 全停止の解除（確認コードつき）。
    //
    // NFR-06, FR-14, UC-06, #1552, IADR-0615: secondFactorCode は利用者が確認の画面に入れた認証アプリの 6 桁（任意・空は null）。
    // 提供側が確かめる（ボットは確かめず、ログ・応答に出さない）。既定の実装はコードを捨てて従来の要求を送る（試験の偽物を壊さない）。
    // 実装（Http*・Grpc*）は必ず上書きし、REST の本文・gRPC の要求の既存の欄で運ぶ。
    Task<KillSwitchResult> DisengageAsync(string reason, string? secondFactorCode, CancellationToken cancellationToken = default) =>
        DisengageAsync(reason, cancellationToken);
}

// FR-14: kill switch 操作の結果。Engaged は操作後の状態。失敗時は Succeeded=false とし、
// 呼び出し側は利用者へ失敗を明示する（成功したように見せない）。
public sealed record KillSwitchResult(bool Succeeded, bool Engaged, string Message);
