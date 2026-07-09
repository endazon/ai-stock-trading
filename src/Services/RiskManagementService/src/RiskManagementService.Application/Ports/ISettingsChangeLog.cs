using AiStockTrading.RiskManagement.Application.State;

namespace AiStockTrading.RiskManagement.Application.Ports;

// FR-11, ADR-0007: 設定・kill switch の変更履歴の記録・照会。実運用では PostgreSQL 監査ログ（#17）。
public interface ISettingsChangeLog
{
    void Record(SettingsChangeEntry entry);

    /// <summary>記録済みの変更履歴を新しい順で返す。</summary>
    IReadOnlyList<SettingsChangeEntry> GetHistory();
}
