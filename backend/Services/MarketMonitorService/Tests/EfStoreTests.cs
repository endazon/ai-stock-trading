using MarketMonitorService.Domain;
using MarketMonitorService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketMonitorService.Tests;

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

    // #286, IADR-0281: 監視銘柄の構成シード（Monitor:SeedSymbols）。
    private static MonitorSeedOptions SeedOptionsWith(params (string Symbol, Market Market)[] symbols) => new()
    {
        SeedSymbols = [.. symbols.Select(s => new MonitorSeedOptions.SeedSymbolEntry { Symbol = s.Symbol, Market = s.Market })],
    };

    [Fact]
    public void 未設定は構成シードが投入される()
    {
        var seed = SeedOptionsWith(("AAPL", Market.UnitedStates));
        var store = new EfMonitoredSymbolStore(NewContext(Guid.NewGuid().ToString()), seed);

        var settings = store.GetSettings();

        settings.MonitoredSymbols.Should().ContainSingle(s => s.Symbol == "AAPL" && s.Market == Market.UnitedStates);
    }

    [Fact]
    public void 未設定でも構成シードが空なら従来どおり空でシードされる()
    {
        var dbName = Guid.NewGuid().ToString();
        var store = new EfMonitoredSymbolStore(NewContext(dbName), new MonitorSeedOptions());

        var settings = store.GetSettings();

        settings.MonitoredSymbols.Should().BeEmpty();
        // ホットパスで無意味な書き込みをしない（Version は初回シードの 1 のまま）。
        using var check = NewContext(dbName);
        check.MonitorSettings.Find(SingletonKeys.Id)!.Version.Should().Be(1);
    }

    [Fact]
    public void 空でも利用者が全削除した記録があれば構成シードで巻き戻らない()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
        {
            // MonitorSettingsSerialization は internal のため、行を直接組み立てず公開 API（Save）だけで
            // 「非空 → 全削除」の遷移を作る（Save が ClearedByUserAt を立てる本体の経路）。
            var store = new EfMonitoredSymbolStore(db);
            store.Save(MonitorDefaults.CreateSettings([new MonitoredSymbol("MSFT", Market.Japan)]));
            store.Save(MonitorDefaults.CreateSettings()); // 全削除 → ClearedByUserAt が立つ
        }

        var seed = SeedOptionsWith(("AAPL", Market.UnitedStates));
        using var check = NewContext(dbName);
        var settings = new EfMonitoredSymbolStore(check, seed).GetSettings();

        settings.MonitoredSymbols.Should().BeEmpty();
        check.MonitorSettings.Find(SingletonKeys.Id)!.Version.Should().Be(2, "触ってはならない（利用者の意思を尊重）");
    }

    [Fact]
    public void 既存の監視銘柄がある行は構成シードの有無に関わらず触られない()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
        {
            new EfMonitoredSymbolStore(db).Save(
                MonitorDefaults.CreateSettings([new MonitoredSymbol("MSFT", Market.UnitedStates)]));
        }

        var seed = SeedOptionsWith(("AAPL", Market.UnitedStates));
        using var check = NewContext(dbName);
        var settings = new EfMonitoredSymbolStore(check, seed).GetSettings();

        settings.MonitoredSymbols.Should().ContainSingle(s => s.Symbol == "MSFT");
        check.MonitorSettings.Find(SingletonKeys.Id)!.Version.Should().Be(1);
    }

    [Fact]
    public void 本機能導入前の空行後方互換_フラグ列がnullのままでも未設定として拾われる()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
        {
            // 本機能導入前のコードが作った行を模す: 構成シード無しの GetSettings は従来どおり空でシードし、
            // ClearedByUserAt は null のままになる（列追加マイグレーションで既存行が両方 null になる後方互換と
            // 同じ状態）。
            new EfMonitoredSymbolStore(db).GetSettings();
        }

        var seed = SeedOptionsWith(("AAPL", Market.UnitedStates));
        using var check = NewContext(dbName);
        var settings = new EfMonitoredSymbolStore(check, seed).GetSettings();

        settings.MonitoredSymbols.Should().ContainSingle(s => s.Symbol == "AAPL");
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
