namespace RiskManagementService.Hosted;

// FR-10, UC-06, ADR-0016 決定7, IADR-0133, #634: 維持率割れ自動縮小の定期評価ドライバの構成。
//
// 既定は**有効**（`WithdrawalEvaluationService`/`ObservedDrawdownRefreshService` の既定無効とは意図的に異なる。
// 新規 IADR〔駆動方式・既定の向き〕参照）。供給元（`IMaintenanceMarginSnapshotSource`）の既定実装
// `UnavailableMaintenanceMarginSnapshotSource` は常に `null` を返し、`MaintenanceMarginReducer.Plan` は
// `snapshot is null` で即座に無動作を返すため（IADR-0133 決定5）、ドライバを既定有効にしても
// **供給元が実装されるまでは 1 回も発動しない**ことが構造的に保証される
// （発注執行サービスの `PositionReconciliationOptions.Enabled` 既定 true と同型）。
//
// `Enabled=false` は「巡回そのものを止めたい」運用上の例外的操作（障害時の緊急停止等）のためだけに残す。
// 本統制の作動可否の単一の制御点は供給元の実装差し替えであり、ドライバ側にもう一段の
// opt-in ゲートを設けると、供給元が実装された日に人間の追加有効化操作を要求してしまい、
// 本 issue（#634・未結線）と同型の再発（登録されているのに動かない）を生む。
public sealed class MaintenanceMarginEvaluationOptions
{
    public const string SectionName = "MaintenanceMarginEvaluation";

    /// <summary>ドライバを起動するか。既定 true（供給元が「供給なし」を返す間は構造的に不活性のため）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>評価間隔（秒）。既定 300（5 分。撤退・実DD の定期評価ドライバと同じ緩やかな周期）。</summary>
    public int IntervalSeconds { get; set; } = 300;
}
