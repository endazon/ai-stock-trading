using MarketMonitorService.Features.MarketMonitor;
using Microsoft.EntityFrameworkCore;

namespace MarketMonitorService.Infrastructure.Persistence;

// FR-04, FR-13, FR-15, ADR-0046 決定 1, #1049, IADR-0442 決定 2: 監視設定の行を**追跡せずに読むだけ**の実装。
// seed の挿入・再 seed はしない（それは `EfMonitoredSymbolStore.GetSettings` の役目であり、読み取り専用の照会から起こしてはならない）。
public sealed class EfMonitorSeedRecord(MarketMonitorDbContext db) : IMonitorSeedRecord
{
    public MonitorSeedState? Read()
    {
        var row = db.MonitorSettings.AsNoTracking().SingleOrDefault(r => r.Id == SingletonKeys.Id);
        if (row is null)
            return null;

        var settings = MonitorSettingsSerialization.Deserialize(row.Json);
        return new MonitorSeedState(row.SeededAt, [.. settings.MonitoredSymbols]);
    }
}
