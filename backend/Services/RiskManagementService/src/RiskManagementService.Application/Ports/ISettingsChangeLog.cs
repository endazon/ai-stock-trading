using RiskManagementService.Application.State;

namespace RiskManagementService.Application.Ports;

// FR-11, ADR-0003, ADR-0007, ADR-0008: 設定・kill switch の変更履歴の記録・照会。実運用は PostgreSQL 実装（EfSettingsChangeLog）が永続化する。
public interface ISettingsChangeLog
{
    void Record(SettingsChangeEntry entry);

    /// <summary>記録済みの変更履歴を新しい順で返す。</summary>
    IReadOnlyList<SettingsChangeEntry> GetHistory();
}
