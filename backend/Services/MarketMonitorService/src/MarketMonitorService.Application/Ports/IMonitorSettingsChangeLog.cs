using AiStockTrading.MarketMonitor.Application.State;

namespace AiStockTrading.MarketMonitor.Application.Ports;

// FR-11, FR-13, ADR-0007: 監視設定（監視銘柄）変更履歴の記録・照会。追記専用。
// 実運用では PostgreSQL の専有 DB（EF 実装）、テストは InMemory 実装で差し替える。
public interface IMonitorSettingsChangeLog
{
    void Record(MonitorSettingsChangeEntry entry);

    /// <summary>記録済みの変更履歴を新しい順で返す。</summary>
    IReadOnlyList<MonitorSettingsChangeEntry> GetHistory();
}
