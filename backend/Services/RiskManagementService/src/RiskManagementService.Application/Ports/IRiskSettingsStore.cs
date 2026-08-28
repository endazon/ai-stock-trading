using RiskManagementService.Domain;

namespace RiskManagementService.Application.Ports;

// FR-10, FR-17, ADR-0003, ADR-0007, ADR-0008: 現行のリスク管理設定の取得・保存。実運用では PostgreSQL 設定ストア（バージョン管理・Slice B）。
// 変更は利用者のみ（RiskSettingsService 経由で履歴を記録する）。生成AI・自動処理は本ポートを直接呼ばない。
public interface IRiskSettingsStore
{
    RiskManagementSettings GetCurrent();

    void Save(RiskManagementSettings settings);
}
