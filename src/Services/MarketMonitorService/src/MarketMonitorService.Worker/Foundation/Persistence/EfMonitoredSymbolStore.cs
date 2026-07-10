using AiStockTrading.MarketMonitor.Application.Ports;
using AiStockTrading.MarketMonitor.Domain;

namespace AiStockTrading.MarketMonitor.Worker.Foundation.Persistence;

// FR-03, FR-13, IADR-0012 踏襲: 監視設定ストアの EF 実装。単一行 JSON＋Version 楽観排他。
// 未設定時は MonitorDefaults をシードして返す。
internal sealed class EfMonitoredSymbolStore(MarketMonitorDbContext db) : IMonitoredSymbolStore
{
    public MarketMonitorSettings GetSettings()
    {
        var row = db.MonitorSettings.Find(SingletonKeys.Id);
        if (row is null)
        {
            var defaults = MonitorDefaults.CreateSettings();
            db.MonitorSettings.Add(new MonitorSettingsRow
            {
                Id = SingletonKeys.Id,
                Json = MonitorSettingsSerialization.Serialize(defaults),
                Version = 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
            return defaults;
        }

        return MonitorSettingsSerialization.Deserialize(row.Json);
    }

    public void Save(MarketMonitorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var row = db.MonitorSettings.Find(SingletonKeys.Id);
        if (row is null)
        {
            db.MonitorSettings.Add(new MonitorSettingsRow
            {
                Id = SingletonKeys.Id,
                Json = MonitorSettingsSerialization.Serialize(settings),
                Version = 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            // IADR-0012: Version をインクリメントし、EF の並行トークンでロストアップデートを防ぐ。
            row.Json = MonitorSettingsSerialization.Serialize(settings);
            row.Version += 1;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        db.SaveChanges();
    }
}
