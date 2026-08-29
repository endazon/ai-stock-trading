using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Features.RiskManagement;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, UC-06, ADR-0003: kill switch 操作の検証。利用者のみ（アクター・理由必須）・変更履歴の記録。
public class KillSwitchServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 6, 0, 0, TimeSpan.Zero);

    private static (KillSwitchService Service, InMemoryKillSwitchStore Store, InMemorySettingsChangeLog Log)
        Create()
    {
        var store = new InMemoryKillSwitchStore();
        var log = new InMemorySettingsChangeLog();
        var clock = new FakeClock(Now, new DateOnly(2026, 7, 9));
        return (new KillSwitchService(store, log, clock), store, log);
    }

    [Fact]
    public void 起動はアクターと理由と日時を状態に残す()
    {
        var (service, store, _) = Create();

        service.Engage("user", "急変のため緊急停止");

        var state = store.GetState();
        state.Engaged.Should().BeTrue();
        state.Actor.Should().Be("user");
        state.Reason.Should().Be("急変のため緊急停止");
        state.ChangedAt.Should().Be(Now);
    }

    [Fact]
    public void 起動と解除は変更履歴に記録される()
    {
        // FR-11: 変更履歴を記録する。
        var (service, _, log) = Create();

        service.Engage("user", "停止");
        service.Disengage("user", "再開");

        var history = log.GetHistory();
        history.Should().HaveCount(2);
        history[0].ChangeType.Should().Be(SettingsChangeType.KillSwitchDisengaged); // 新しい順
        history[1].ChangeType.Should().Be(SettingsChangeType.KillSwitchEngaged);
        history[1].Actor.Should().Be("user");
    }

    [Theory]
    [InlineData("", "reason")]
    [InlineData("user", "")]
    [InlineData("  ", "reason")]
    public void アクターまたは理由が空なら操作を拒否する(string actor, string reason)
    {
        // FR-11: 監査性のためアクター・理由のない変更は受け付けない。
        var (service, _, _) = Create();

        var act = () => service.Engage(actor, reason);

        act.Should().Throw<ArgumentException>();
    }
}
