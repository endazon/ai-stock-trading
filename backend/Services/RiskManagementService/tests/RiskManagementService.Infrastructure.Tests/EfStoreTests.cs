using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.RiskManagement.Infrastructure.Foundation.Persistence;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiStockTrading.RiskManagement.Infrastructure.Tests;

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

    // FR-20 (3), #422, IADR-0161: **新規インストールの発注先は内蔵 paper**（外部へ一度も発注しない唯一の値）。
    // シード経路（設定行がまだ無い状態）を通した結果を固定する——既定の定義（RiskManagementSettings）が
    // 正しくても、シードが別の値を書けば意味が無い。
    [Fact]
    public void 新規インストールの発注先は内蔵paperである()
    {
        var dbName = Guid.NewGuid().ToString();

        using (var db = NewContext(dbName))
        {
            new EfRiskSettingsStore(db).GetCurrent()
                .BrokerProvider.Should().Be(Shared.Contracts.Trading.BrokerProvider.InternalPaper);
        }

        // シードされた行を読み直しても同じであること（書いた値が実弾でない）。
        using (var db2 = NewContext(dbName))
        {
            new EfRiskSettingsStore(db2).GetCurrent()
                .BrokerProvider.Should().NotBe(Shared.Contracts.Trading.BrokerProvider.MoomooReal);
        }
    }

    // FR-20 (3), #422: **発注先の項目を持たない旧行**（#334 以前に書かれた行）を DB に置いた状態で読む。
    // 直列化単体ではなくストア経由で固定するのは、旧行の読み出しが実際に通る経路だからである。
    [Fact]
    public void 発注先を持たない旧行はストア経由でも内蔵paperとして読まれる()
    {
        var dbName = Guid.NewGuid().ToString();
        var legacyJson = System.Text.RegularExpressions.Regex.Replace(
            RiskSettingsSerialization.Serialize(TradingDefaults.CreateSettings()),
            ",\"brokerProvider\":\\d+",
            string.Empty);
        legacyJson.Should().NotContain("brokerProvider", "旧行の再現に失敗している");

        using (var db = NewContext(dbName))
        {
            db.RiskSettings.Add(new RiskSettingsRow
            {
                Id = SingletonKeys.Id,
                Json = legacyJson,
                Version = 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }

        using (var db2 = NewContext(dbName))
        {
            new EfRiskSettingsStore(db2).GetCurrent()
                .BrokerProvider.Should().Be(
                    Shared.Contracts.Trading.BrokerProvider.InternalPaper,
                    "「読めない行は実弾」に倒れる移行は取り返しがつかない（FR-20 (3)）");
        }
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
