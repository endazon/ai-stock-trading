using AiStockTrading.MarketMonitor.Domain;
using AiStockTrading.MarketMonitor.Infrastructure.Foundation.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiStockTrading.MarketMonitor.Infrastructure.Tests;

// FR-03, FR-13, IADR-0012: EF ストアの永続化を InMemory DB で検証する（設定・基準値・クールダウン）。
public class EfStoreTests
{
    private static MarketMonitorDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<MarketMonitorDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    [Fact]
    public void 設定は未設定時に既定値をシードして返す()
    {
        var store = new EfMonitoredSymbolStore(NewContext(Guid.NewGuid().ToString()));

        var settings = store.GetSettings();

        settings.MovementThresholdRatio.Should().Be(MonitorDefaults.MovementThresholdRatio);
        settings.Cooldown.Should().Be(MonitorDefaults.Cooldown);
    }

    [Fact]
    public void 設定の保存はラウンドトリップし版番号が増える()
    {
        var dbName = Guid.NewGuid().ToString();
        var updated = new MarketMonitorSettings
        {
            MovementThresholdRatio = 0.05m,
            Cooldown = TimeSpan.FromMinutes(10),
            MonitoredSymbols = [new MonitoredSymbol("AAPL", Market.UnitedStates)],
        };

        using (var db = NewContext(dbName))
        {
            var store = new EfMonitoredSymbolStore(db);
            store.GetSettings();     // シード（Version=1）
            store.Save(updated);     // Version=2
        }

        using var check = NewContext(dbName);
        var reloaded = new EfMonitoredSymbolStore(check).GetSettings();
        reloaded.MovementThresholdRatio.Should().Be(0.05m);
        reloaded.MonitoredSymbols.Should().ContainSingle(s => s.Symbol == "AAPL");
        check.MonitorSettings.Find(SingletonKeys.Id)!.Version.Should().Be(2);
    }

    [Fact]
    public void 基準値はラウンドトリップする()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
        {
            new EfPriceBaselineStore(db).SetBaseline("AAPL", Market.UnitedStates, 1_234m);
        }
        using var db2 = NewContext(dbName);
        new EfPriceBaselineStore(db2).GetBaseline("AAPL", Market.UnitedStates).Should().Be(1_234m);
    }

    [Fact]
    public void 基準値は更新できる()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
        {
            var store = new EfPriceBaselineStore(db);
            store.SetBaseline("AAPL", Market.UnitedStates, 1_000m);
            store.SetBaseline("AAPL", Market.UnitedStates, 1_100m);
        }
        using var db2 = NewContext(dbName);
        new EfPriceBaselineStore(db2).GetBaseline("AAPL", Market.UnitedStates).Should().Be(1_100m);
    }

    [Fact]
    public void クールダウンはラウンドトリップする()
    {
        var dbName = Guid.NewGuid().ToString();
        var at = new DateTimeOffset(2026, 7, 10, 1, 0, 0, TimeSpan.Zero);
        using (var db = NewContext(dbName))
        {
            new EfCooldownStore(db).SetLastTriggered("AAPL", Market.UnitedStates, at);
        }
        using var db2 = NewContext(dbName);
        new EfCooldownStore(db2).GetLastTriggered("AAPL", Market.UnitedStates).Should().Be(at);
    }
}
