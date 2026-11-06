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

    // 全停止の解除（確認コードと見た状態の版つき）。
    //
    // FR-10, FR-14, #1561, IADR-0620: expectedVersion は確認を出したときに照会した kill switch の版（null は前提条件なし＝従来）。
    // 提供側は、渡された版が今の版と違えば 409 で何も変えない（段に依らない）。段 2 は版の無い解除を 428 で拒む。
    // 既定の実装は版を捨てて従来の口へ委ねる（試験の偽物を壊さない）。実装（Http*・Grpc*）は必ず上書きする。
    Task<KillSwitchResult> DisengageAsync(
        string reason, string? secondFactorCode, string? expectedVersion, CancellationToken cancellationToken = default) =>
        DisengageAsync(reason, secondFactorCode, cancellationToken);

    // FR-10, FR-14, #1561, IADR-0620: 今の kill switch の版（稼働状態の照会の kill_switch_version）。照会に失敗したら null
    // （確認と解除は版なしで従来どおり進む。段 1 では通る）。既定の実装は null（試験の偽物を壊さない）。
    Task<string?> GetVersionAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
}

// FR-14: kill switch 操作の結果。Engaged は操作後の状態。失敗時は Succeeded=false とし、
// 呼び出し側は利用者へ失敗を明示する（成功したように見せない）。
public sealed record KillSwitchResult(bool Succeeded, bool Engaged, string Message);
