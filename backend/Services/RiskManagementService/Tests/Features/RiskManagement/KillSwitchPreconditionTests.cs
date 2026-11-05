using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Features.RiskManagement;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// 🔴 FR-10, FR-14, #1534, IADR-0620: kill switch の発動・解除は「見た状態」の版を前提条件に取れる。古い版を見た操作は状態を変えない。
// 段 1（既定）は前提条件の無い要求も従前どおり受け、段 2 は前提条件の無い解除だけを拒否する（発動は安全側のため求めない）。
public class KillSwitchPreconditionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 6, 0, 0, TimeSpan.Zero);

    private static (KillSwitchService Service, InMemoryKillSwitchStore Store, FakeClock Clock) Create(
        KillSwitchPreconditionMode mode = KillSwitchPreconditionMode.Report)
    {
        var store = new InMemoryKillSwitchStore();
        var clock = new FakeClock(T0, DateOnly.FromDateTime(T0.UtcDateTime));
        return (new KillSwitchService(store, new InMemorySettingsChangeLog(), clock, preconditions: new KillSwitchPreconditionOptions(mode)), store, clock);
    }

    [Fact]
    public void 版は書くたびに変わり_未操作は0()
    {
        var (service, _, clock) = Create();
        service.GetState().Version.Should().Be("0");

        service.Engage("owner", "急変");
        var engaged = service.GetState().Version;
        engaged.Should().NotBe("0");

        clock.UtcNow = T0.AddMinutes(1);
        service.Disengage("owner", "回復", expectedVersion: engaged);
        service.GetState().Version.Should().NotBe(engaged);
    }

    [Fact]
    public void 古い版を見た解除は409で拒否され_状態は変わらない()
    {
        // 利用者 A が「発動中（版 v1）」を見た後に、B が解除して再発動した（版 v3）。A の解除は v1 を前提にしているので通さない。
        var (service, store, clock) = Create();
        service.Engage("A", "急変");
        var seenByA = service.GetState().Version;
        clock.UtcNow = T0.AddMinutes(1);
        service.Disengage("B", "回復", expectedVersion: seenByA);
        clock.UtcNow = T0.AddMinutes(2);
        service.Engage("B", "再度の急変", service.GetState().Version);
        var before = store.GetState();

        clock.UtcNow = T0.AddMinutes(3);
        var stale = () => service.Disengage("A", "回復したはず", expectedVersion: seenByA);

        stale.Should().Throw<KillSwitchVersionConflictException>();
        store.GetState().Should().Be(before, "古い版を見た操作は何も書かない");
        store.GetState().Engaged.Should().BeTrue();
    }

    [Fact]
    public void 古い版を見た発動も409で拒否される()
    {
        var (service, _, clock) = Create();
        var seen = service.GetState().Version;
        service.Engage("B", "急変", seen);
        clock.UtcNow = T0.AddMinutes(1);

        var stale = () => service.Engage("A", "急変", seen);

        stale.Should().Throw<KillSwitchVersionConflictException>();
        service.GetState().Actor.Should().Be("B");
    }

    [Fact]
    public void 段1では前提条件の無い発動と解除も従前どおり通る()
    {
        var (service, _, clock) = Create(KillSwitchPreconditionMode.Report);
        service.Engage("owner", "急変");
        clock.UtcNow = T0.AddMinutes(1);
        service.Disengage("owner", "回復");

        service.GetState().Engaged.Should().BeFalse();
    }

    [Fact]
    public void 段2では前提条件の無い解除を拒否し_発動は求めない()
    {
        var (service, _, clock) = Create(KillSwitchPreconditionMode.Require);
        service.Engage("owner", "急変");
        service.GetState().Engaged.Should().BeTrue("発動は安全側の操作であり、段 2 でも前提条件を求めない");
        clock.UtcNow = T0.AddMinutes(1);

        var unconditioned = () => service.Disengage("owner", "回復");
        unconditioned.Should().Throw<KillSwitchPreconditionRequiredException>();
        service.GetState().Engaged.Should().BeTrue();

        service.Disengage("owner", "回復", expectedVersion: service.GetState().Version);
        service.GetState().Engaged.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, KillSwitchPreconditionMode.Report)]
    [InlineData("", KillSwitchPreconditionMode.Report)]
    [InlineData("Report", KillSwitchPreconditionMode.Report)]
    [InlineData("Require", KillSwitchPreconditionMode.Require)]
    public void 段の構成を読む(string? value, KillSwitchPreconditionMode expected) =>
        KillSwitchPreconditionOptions.From(value).Mode.Should().Be(expected);

    [Fact]
    public void 知らない段は起動で止める()
    {
        var read = () => KillSwitchPreconditionOptions.From("Strict");
        read.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task 並行する同じ版の解除は1つだけが通る()
    {
        var (service, store, _) = Create();
        service.Engage("owner", "急変");
        var seen = service.GetState().Version;

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            store.TrySetState(new KillSwitchState(false, $"u{i}", "回復", T0.AddSeconds(i + 1)), seen))));

        results.Count(r => r).Should().Be(1, "比較と書き込みは同じ錠の中で行う");
    }
}
