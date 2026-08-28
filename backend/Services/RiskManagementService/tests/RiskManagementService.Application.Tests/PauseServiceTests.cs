using RiskManagementService.Application.Adapters;
using RiskManagementService.Application.Services;
using RiskManagementService.Application.State;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Application.Tests;

// FR-10, FR-14, UC-06, ADR-0009: 一時停止（pause）/再開（resume）操作の検証。
// 利用者のみ（アクター・理由必須）・変更履歴の記録・冪等性。
public class PauseServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 6, 0, 0, TimeSpan.Zero);

    private static (PauseService Service, InMemoryPauseStore Store, InMemorySettingsChangeLog Log)
        Create()
    {
        var store = new InMemoryPauseStore();
        var log = new InMemorySettingsChangeLog();
        var clock = new FakeClock(Now, new DateOnly(2026, 7, 9));
        return (new PauseService(store, log, clock), store, log);
    }

    [Fact]
    public void 一時停止はアクターと理由と日時を状態に残す()
    {
        var (service, store, _) = Create();

        service.Pause("user", "様子見のため一時停止");

        var state = store.GetState();
        state.Paused.Should().BeTrue();
        state.Actor.Should().Be("user");
        state.Reason.Should().Be("様子見のため一時停止");
        state.ChangedAt.Should().Be(Now);
    }

    [Fact]
    public void 再開で非停止へ戻る()
    {
        var (service, store, _) = Create();

        service.Pause("user", "停止");
        service.Resume("user", "再開");

        store.GetState().Paused.Should().BeFalse();
    }

    [Fact]
    public void 一時停止と再開は変更履歴に記録される()
    {
        // FR-11, ADR-0009: 監査（アクター・理由・日時）を設定変更履歴へ残す（kill switch と同経路）。
        var (service, _, log) = Create();

        service.Pause("user", "様子見");
        service.Resume("user", "再開");

        var history = log.GetHistory();
        history.Should().HaveCount(2);
        history[0].ChangeType.Should().Be(SettingsChangeType.TradingResumed); // 新しい順
        history[1].ChangeType.Should().Be(SettingsChangeType.TradingPaused);
        history[1].Actor.Should().Be("user");
        history[1].Reason.Should().Be("様子見");
    }

    [Fact]
    public void 停止中の再停止は冪等で副作用がない()
    {
        // ADR-0009 受け入れ基準: 停止中の再 /pause は状態を返すのみ（ストア更新も履歴記録もしない）。
        var (service, store, log) = Create();

        service.Pause("user", "停止");
        var again = service.Pause("other", "重複停止");

        // 状態は最初の停止のまま（アクター・理由が上書きされない）。
        again.Paused.Should().BeTrue();
        again.Actor.Should().Be("user");
        store.GetState().Actor.Should().Be("user");
        // 履歴は 1 件のみ（重複操作で水増ししない）。
        log.GetHistory().Should().HaveCount(1);
    }

    [Fact]
    public void 非停止中の再開は冪等で副作用がない()
    {
        // ADR-0009 受け入れ基準: 非停止中の /resume も状態を返すのみで副作用なし。
        var (service, _, log) = Create();

        var result = service.Resume("user", "再開");

        result.Paused.Should().BeFalse();
        log.GetHistory().Should().BeEmpty();
    }

    [Fact]
    public void 既定は非停止である()
    {
        // 安全既定: 状態未設定なら取引可能側ではなく「非停止（＝新規建てを止めない）」が既定。
        // pause は「止める」側の統制であり、既定が非停止であることは「不明時に取引を止めない」ではなく
        // 「不明時に pause 由来の停止を主張しない」意味（他の統制＝kill switch/lockout は別に評価される）。
        var (_, store, _) = Create();

        store.GetState().Paused.Should().BeFalse();
    }

    [Theory]
    [InlineData("", "reason")]
    [InlineData("user", "")]
    [InlineData("  ", "reason")]
    public void アクターまたは理由が空なら操作を拒否する(string actor, string reason)
    {
        // FR-11: 監査性のためアクター・理由のない変更は受け付けない。
        var (service, _, _) = Create();

        var act = () => service.Pause(actor, reason);

        act.Should().Throw<ArgumentException>();
    }
}
