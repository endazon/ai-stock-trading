using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.RiskManagement.Worker.Foundation.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiStockTrading.RiskManagement.Worker.Tests;

// FR-10, FR-11, IADR-0012: EF ストアの永続化を InMemory DB で検証する（設定・kill switch・ロックアウト・履歴）。
public class EfStoreTests
{
    // 同一 DB 名を共有する複数コンテキストで「別スコープでも読める」永続化を検証する。
    private static RiskManagementDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    [Fact]
    public void 設定は未設定時に既定値をシードして返す()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfRiskSettingsStore(db);

        var settings = store.GetCurrent();

        settings.Limits.MaxOpenPositions.Should().Be(TradingDefaults.CreateRiskLimits().MaxOpenPositions);
        settings.Guard.EnabledProductTypes.Should().Contain(Shared.Contracts.Trading.ProductType.Cash);
    }

    [Fact]
    public void 設定の保存はラウンドトリップし別コンテキストからも読める()
    {
        var dbName = Guid.NewGuid().ToString();
        var updated = TradingDefaults.CreateRiskLimits() with { MaxOpenPositions = 7 };

        using (var db = NewContext(dbName))
        {
            var store = new EfRiskSettingsStore(db);
            var current = store.GetCurrent();
            store.Save(current with { Limits = updated });
        }

        using (var db2 = NewContext(dbName))
        {
            var reloaded = new EfRiskSettingsStore(db2).GetCurrent();
            reloaded.Limits.MaxOpenPositions.Should().Be(7);
        }
    }

    [Fact]
    public void 設定保存のたびに版番号が増える()
    {
        // IADR-0012: Version を楽観的排他制御のトークンとして増分する。
        var dbName = Guid.NewGuid().ToString();
        var db = NewContext(dbName);
        var store = new EfRiskSettingsStore(db);

        store.GetCurrent();                       // シード（Version=1）
        var settings = store.GetCurrent();
        store.Save(settings);                      // Version=2
        store.Save(settings);                      // Version=3

        using var check = NewContext(dbName);
        check.RiskSettings.Find(SingletonKeys.Id)!.Version.Should().Be(3);
    }

    [Fact]
    public void kill_switch_状態はラウンドトリップする()
    {
        var dbName = Guid.NewGuid().ToString();
        var changedAt = DateTimeOffset.UtcNow;

        using (var db = NewContext(dbName))
        {
            new EfKillSwitchStore(db).SetState(new KillSwitchState(true, "user", "停止", changedAt));
        }

        using var db2 = NewContext(dbName);
        var state = new EfKillSwitchStore(db2).GetState();
        state.Engaged.Should().BeTrue();
        state.Actor.Should().Be("user");
        state.Reason.Should().Be("停止");
    }

    [Fact]
    public void 一時停止状態はラウンドトリップする()
    {
        // FR-10, ADR-0009: pause 状態を単一行で永続化し、別コンテキストからも読める。
        var dbName = Guid.NewGuid().ToString();
        var changedAt = DateTimeOffset.UtcNow;

        using (var db = NewContext(dbName))
        {
            new EfPauseStore(db).SetState(new PauseState(true, "user", "様子見", changedAt));
        }

        using var db2 = NewContext(dbName);
        var state = new EfPauseStore(db2).GetState();
        state.Paused.Should().BeTrue();
        state.Actor.Should().Be("user");
        state.Reason.Should().Be("様子見");
    }

    [Fact]
    public void 一時停止は未設定時に非停止を返す()
    {
        // 安全既定: 行が無ければ NotPaused（pause 由来の停止を主張しない）。
        var db = NewContext(Guid.NewGuid().ToString());

        new EfPauseStore(db).GetState().Paused.Should().BeFalse();
    }

    [Fact]
    public void ロックアウトは設定_取得_解除できる()
    {
        var dbName = Guid.NewGuid().ToString();
        var lockout = new LockoutState(new DateOnly(2026, 7, 10), "日次損失上限", DateTimeOffset.UtcNow);

        using (var db = NewContext(dbName))
        {
            new EfLockoutStore(db).Set(lockout);
        }
        using (var db2 = NewContext(dbName))
        {
            new EfLockoutStore(db2).Get()!.ReleaseOn.Should().Be(new DateOnly(2026, 7, 10));
        }
        using (var db3 = NewContext(dbName))
        {
            new EfLockoutStore(db3).Clear();
        }
        using var db4 = NewContext(dbName);
        new EfLockoutStore(db4).Get().Should().BeNull();
    }

    [Fact]
    public void 変更履歴は追記され新しい順で返る()
    {
        // FR-11: 監査。新しい順（ChangedAt 降順）。
        var dbName = Guid.NewGuid().ToString();
        var db = NewContext(dbName);
        var log = new EfSettingsChangeLog(db);
        var t0 = DateTimeOffset.UtcNow;

        log.Record(new SettingsChangeEntry("user", SettingsChangeType.KillSwitchEngaged, "停止", t0));
        log.Record(new SettingsChangeEntry("user", SettingsChangeType.Limits, "上限変更", t0.AddMinutes(1)));

        var history = log.GetHistory();
        history.Should().HaveCount(2);
        history[0].ChangeType.Should().Be(SettingsChangeType.Limits);       // 新しい方が先頭
        history[1].ChangeType.Should().Be(SettingsChangeType.KillSwitchEngaged);
    }
}
