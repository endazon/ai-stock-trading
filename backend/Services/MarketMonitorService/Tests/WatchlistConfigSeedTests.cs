using MarketMonitorService.Domain;
using MarketMonitorService.Features.MarketMonitor;
using MarketMonitorService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketMonitorService.Tests;

// FR-02, FR-13, UC-06, #286, IADR-0281: watchlist の初回シード（構成 Monitor:SeedSymbols）と、
// 「利用者が明示的に全削除した」意思の尊重（ClearedByUserAt）を、実際の変更経路
// （MonitorWatchlistService.Add/Remove）を通して EfMonitoredSymbolStore（実体）で検証する。
// EfStoreTests は永続層単体（GetSettings/Save の判定ロジック）を検証するのに対し、本ファイルは
// 「DELETE で最後の 1 件を消す → 再取得しても構成シードで巻き戻らない → 追加で解除される」という
// 業務フロー全体の回帰を固定する。
public class WatchlistConfigSeedTests
{
    private static MarketMonitorDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<MarketMonitorDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    private static MonitorSeedOptions SeedOptionsWith(params (string Symbol, Market Market)[] symbols) => new()
    {
        SeedSymbols = [.. symbols.Select(s => new MonitorSeedOptions.SeedSymbolEntry { Symbol = s.Symbol, Market = s.Market })],
    };

    [Fact]
    public void 最後の1件をDELETEすると全削除フラグが立ち構成シードで巻き戻らない()
    {
        var dbName = Guid.NewGuid().ToString();
        var seed = SeedOptionsWith(("AAPL", Market.UnitedStates));

        using (var db = NewContext(dbName))
        {
            var store = new EfMonitoredSymbolStore(db, seed);
            var svc = new MonitorWatchlistService(store, new InMemoryMonitorSettingsChangeLog(), new FakeClock(DateTimeOffset.UnixEpoch));

            store.GetSettings().MonitoredSymbols.Should().ContainSingle(s => s.Symbol == "AAPL", "初回シードが投入されている前提");

            svc.Remove("AAPL", Market.UnitedStates, "endazon", "監視終了");
        }

        // 別スコープ（DbContext を作り直す＝サービス再起動を模す）で再取得しても、構成シードに巻き戻らない。
        using var reread = NewContext(dbName);
        var reseeded = new EfMonitoredSymbolStore(reread, seed).GetSettings();

        reseeded.MonitoredSymbols.Should().BeEmpty("利用者が全削除した意思を尊重し、構成シードで巻き戻してはならない");
    }

    [Fact]
    public void 全削除後に追加すると再度空にしたときだけフラグが立ち直す()
    {
        var dbName = Guid.NewGuid().ToString();
        var seed = SeedOptionsWith(("AAPL", Market.UnitedStates));
        var log = new InMemoryMonitorSettingsChangeLog();

        using (var db = NewContext(dbName))
        {
            var store = new EfMonitoredSymbolStore(db, seed);
            var svc = new MonitorWatchlistService(store, log, new FakeClock(DateTimeOffset.UnixEpoch));
            store.GetSettings(); // シード
            svc.Remove("AAPL", Market.UnitedStates, "endazon", "監視終了");
            svc.Add("MSFT", Market.UnitedStates, "endazon", "再開");
        }

        using (var reread = NewContext(dbName))
        {
            var settings = new EfMonitoredSymbolStore(reread, seed).GetSettings();
            settings.MonitoredSymbols.Should().ContainSingle(s => s.Symbol == "MSFT", "追加により全削除フラグは解除される");
        }

        // 解除後に MSFT を消して再び空にすると、今度こそフラグが立ち直し、
        // AAPL の構成シードで巻き戻らない（AAPL が「復活」してはならない）。
        using (var db = NewContext(dbName))
        {
            var store = new EfMonitoredSymbolStore(db, seed);
            var svc = new MonitorWatchlistService(store, log, new FakeClock(DateTimeOffset.UnixEpoch));
            svc.Remove("MSFT", Market.UnitedStates, "endazon", "再度終了");
        }

        using var check = NewContext(dbName);
        var final = new EfMonitoredSymbolStore(check, seed).GetSettings();
        final.MonitoredSymbols.Should().BeEmpty("再度の全削除も利用者の意思として尊重し構成シードへ巻き戻らない");
    }

    [Fact]
    public void 全置換PUT相当のSaveで空にしても全削除として扱われる()
    {
        var dbName = Guid.NewGuid().ToString();
        var seed = SeedOptionsWith(("AAPL", Market.UnitedStates));

        using (var db = NewContext(dbName))
        {
            var store = new EfMonitoredSymbolStore(db, seed);
            var current = store.GetSettings(); // シード（AAPL）
            store.Save(current with { MonitoredSymbols = [] }); // 全置換 PUT が監視銘柄を空にした場合を模す
        }

        using var reread = NewContext(dbName);
        var settings = new EfMonitoredSymbolStore(reread, seed).GetSettings();

        settings.MonitoredSymbols.Should().BeEmpty("DELETE 経路以外（全置換 PUT）で空にした場合も利用者の意思として扱う");
    }

    [Fact]
    public void 監視銘柄以外の部分更新は全削除フラグを新たに立てない()
    {
        var dbName = Guid.NewGuid().ToString();
        // 構成シードは空（既に空の状態を作るため）。
        using var db = NewContext(dbName);
        var store = new EfMonitoredSymbolStore(db);
        var current = store.GetSettings(); // 空でシード
        current.MonitoredSymbols.Should().BeEmpty();

        // 変動閾値だけを変える部分更新（監視銘柄は変わらず空のまま）。
        store.Save(current with { MovementThresholdRatio = 0.05m });

        var row = db.MonitorSettings.Find(SingletonKeys.Id)!;
        row.ClearedByUserAt.Should().BeNull("監視銘柄が変化していない部分更新ではフラグを新たに立てない");
    }
}
