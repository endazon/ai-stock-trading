using ConfigurationService.Application;
using ConfigurationService.Application.State;
using ConfigurationService.Infrastructure.Persistence;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ConfigurationService.Infrastructure.Tests;

// FR-17, IADR-0012/0021: 前提条件 EF ストア（シード・往復・Version 増分・楽観排他）と変更履歴を InMemory DB で検証する。
public class EfAssumptionsStoreTests
{
    private static ConfigurationDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<ConfigurationDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    [Fact]
    public void 未設定時は既定値を_Version1_でシードして返す()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfAssumptionsStore(db);

        var current = store.GetCurrent();

        current.Version.Should().Be(1);
        current.Assumptions.CapitalGainsTaxRate.Should().Be(0.20315m);
    }

    [Fact]
    public void 更新はラウンドトリップし_Version_が上がる()
    {
        var dbName = Guid.NewGuid().ToString();
        var modified = TradingAssumptionsDefaults.Create() with { FxSpreadRatio = 0.0025m };

        using (var db = NewContext(dbName))
        {
            var store = new EfAssumptionsStore(db);
            store.GetCurrent(); // シード（v1）
            store.Save(modified, expectedVersion: 1).Should().Be(2);
        }

        using var db2 = NewContext(dbName);
        var reloaded = new EfAssumptionsStore(db2).GetCurrent();
        reloaded.Version.Should().Be(2);
        reloaded.Assumptions.FxSpreadRatio.Should().Be(0.0025m);
    }

    [Fact]
    public void 未シード状態の_Save_は既定を_v1_でシードしてから更新する()
    {
        // GET を経ずに最初のリクエストが PUT のケース（自動化スクリプトの初期登録等）。500 にならず v2 になる。
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfAssumptionsStore(db);
        var modified = TradingAssumptionsDefaults.Create() with { FxSpreadRatio = 0.004m };

        var version = store.Save(modified, expectedVersion: 1);

        version.Should().Be(2);
        store.GetCurrent().Assumptions.FxSpreadRatio.Should().Be(0.004m);
    }

    [Fact]
    public void 版番号が不一致の更新は競合で弾かれる()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfAssumptionsStore(db);
        store.GetCurrent(); // v1

        var act = () => store.Save(TradingAssumptionsDefaults.Create(), expectedVersion: 99);

        act.Should().Throw<AssumptionsConcurrencyException>();
    }

    [Fact]
    public void 変更履歴は追記され新しい順で返る()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var log = new EfAssumptionsChangeLog(db);
        var t0 = DateTimeOffset.UtcNow;

        log.Record(new AssumptionsChangeEntry("owner", "初回", t0, 2, "a", "b"));
        log.Record(new AssumptionsChangeEntry("owner", "二回目", t0.AddMinutes(1), 3, "b", "c"));

        var history = log.GetHistory();
        history.Should().HaveCount(2);
        history[0].Reason.Should().Be("二回目"); // 新しい方が先頭
        history[0].Version.Should().Be(3);
    }
}
