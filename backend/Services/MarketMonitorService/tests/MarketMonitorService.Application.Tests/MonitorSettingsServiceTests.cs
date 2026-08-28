using MarketMonitorService.Application.Adapters;
using MarketMonitorService.Application.Services;
using MarketMonitorService.Application.State;
using MarketMonitorService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace MarketMonitorService.Application.Tests;

// FR-03, FR-11, FR-13, UC-06, SC-01 §2, #340, IADR-0155:
// 収集パラメータ（変動閾値・クールダウン）の部分更新。理由必須・値域検証・履歴記録を確認する。
//
// **部分更新であること（他の項目を巻き込まないこと）が本テストの主眼である。**
// 既存の全置換 PUT を画面から使うと、変動閾値だけを変えたい場面で監視銘柄を送り直す必要があり、
// 送り漏らした瞬間に監視銘柄が消える。
public class MonitorSettingsServiceTests
{
    private static MonitorSettingsService NewService(
        out InMemoryMonitoredSymbolStore store,
        out InMemoryMonitorSettingsChangeLog log,
        params MonitoredSymbol[] initial)
    {
        store = new InMemoryMonitoredSymbolStore(MonitorDefaults.CreateSettings(initial));
        log = new InMemoryMonitorSettingsChangeLog();
        return new MonitorSettingsService(store, log, new FakeClock(DateTimeOffset.UnixEpoch));
    }

    // T-340-20: 変動閾値を変更でき、ストアと履歴（種別・アクター・理由・前後値）に反映される。
    [Fact]
    public void 変動閾値を変更できストアと履歴に反映される()
    {
        var svc = NewService(out var store, out var log);

        var updated = svc.UpdateMovementThreshold(0.05m, "endazon", "ボラティリティ上昇に合わせて引き上げる");

        updated.MovementThresholdRatio.Should().Be(0.05m);
        store.GetSettings().MovementThresholdRatio.Should().Be(0.05m);
        log.GetHistory().Should().ContainSingle(e =>
            e.ChangeType == MonitorSettingsChangeType.MovementThresholdChanged
            && e.Actor == "endazon"
            && e.Reason == "ボラティリティ上昇に合わせて引き上げる"
            && e.Before == "0.03"
            && e.After == "0.05");
    }

    // T-340-21: **部分更新であり監視銘柄とクールダウンを巻き込まない**（全置換 PUT との決定的な違い）。
    [Fact]
    public void 変動閾値の変更は監視銘柄とクールダウンを巻き込まない()
    {
        var svc = NewService(out var store, out _, new MonitoredSymbol("AAPL", Market.UnitedStates));
        var before = store.GetSettings();

        svc.UpdateMovementThreshold(0.05m, "endazon", "引き上げ");

        var after = store.GetSettings();
        after.MonitoredSymbols.Should().ContainSingle(s => s.Symbol == "AAPL");
        after.Cooldown.Should().Be(before.Cooldown);
    }

    // T-340-22: **理由が空なら保存しない**（監査のため理由必須・FR-11）。履歴も残らない。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 理由が空なら変動閾値を保存しない(string reason)
    {
        var svc = NewService(out var store, out var log);

        var act = () => svc.UpdateMovementThreshold(0.05m, "endazon", reason);

        act.Should().Throw<ArgumentException>();
        store.GetSettings().MovementThresholdRatio.Should().Be(MonitorDefaults.MovementThresholdRatio);
        log.GetHistory().Should().BeEmpty();
    }

    // T-340-23: 値域外（0 以下・上限超）は保存しない。**黙って既定値へ丸めない。**
    [Theory]
    [InlineData(0)]
    [InlineData(-0.01)]
    [InlineData(0.51)]
    public void 値域外の変動閾値は保存しない(double ratio)
    {
        var svc = NewService(out var store, out var log);

        var act = () => svc.UpdateMovementThreshold((decimal)ratio, "endazon", "検証");

        act.Should().Throw<ArgumentException>();
        store.GetSettings().MovementThresholdRatio.Should().Be(MonitorDefaults.MovementThresholdRatio);
        log.GetHistory().Should().BeEmpty();
    }

    // T-340-24: 値域の境界（上限ちょうど）は保存できる（上限を含む・MonitorSettingsBounds）。
    [Fact]
    public void 上限ちょうどの変動閾値は保存できる()
    {
        var svc = NewService(out var store, out _);

        svc.UpdateMovementThreshold(MonitorSettingsBounds.MaxMovementThresholdRatio, "endazon", "上限で検証");

        store.GetSettings().MovementThresholdRatio.Should().Be(MonitorSettingsBounds.MaxMovementThresholdRatio);
    }

    // T-340-25: クールダウンも同じ規律（理由必須・値域検証・履歴・部分更新）で変更できる。
    [Fact]
    public void クールダウンを変更でき変動閾値を巻き込まない()
    {
        var svc = NewService(out var store, out var log);

        svc.UpdateCooldown(TimeSpan.FromMinutes(30), "endazon", "再トリガー抑制を延ばす");

        store.GetSettings().Cooldown.Should().Be(TimeSpan.FromMinutes(30));
        store.GetSettings().MovementThresholdRatio.Should().Be(MonitorDefaults.MovementThresholdRatio);
        log.GetHistory().Should().ContainSingle(e => e.ChangeType == MonitorSettingsChangeType.CooldownChanged);
    }

    // T-340-26: 負のクールダウン・上限超は保存しない。
    [Fact]
    public void 値域外のクールダウンは保存しない()
    {
        var svc = NewService(out var store, out var log);

        var negative = () => svc.UpdateCooldown(TimeSpan.FromMinutes(-1), "endazon", "検証");
        var tooLong = () => svc.UpdateCooldown(MonitorSettingsBounds.MaxCooldown + TimeSpan.FromMinutes(1), "endazon", "検証");

        negative.Should().Throw<ArgumentException>();
        tooLong.Should().Throw<ArgumentException>();
        store.GetSettings().Cooldown.Should().Be(MonitorDefaults.Cooldown);
        log.GetHistory().Should().BeEmpty();
    }

    // T-340-27: 履歴は監視銘柄の変更と**同じ台帳**に載る（SC-01 §2 と SC-02 の履歴が食い違わない）。
    [Fact]
    public void 収集パラメータの履歴は監視銘柄と同じ台帳に載る()
    {
        var store = new InMemoryMonitoredSymbolStore(MonitorDefaults.CreateSettings());
        var log = new InMemoryMonitorSettingsChangeLog();
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        var watchlist = new MonitorWatchlistService(store, log, clock);
        var settings = new MonitorSettingsService(store, log, clock);

        watchlist.Add("AAPL", Market.UnitedStates, "endazon", "監視開始");
        settings.UpdateMovementThreshold(0.05m, "endazon", "引き上げ");

        settings.GetHistory().Should().HaveCount(2);
    }
}
