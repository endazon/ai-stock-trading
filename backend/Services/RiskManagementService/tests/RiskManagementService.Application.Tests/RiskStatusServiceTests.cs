using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-10, UC-07, ADR-0009: `/status` 集約の検証。3 統制の状態・優先順位・段階・当日損益・上限使用率・ポジションを束ねる。
public class RiskStatusServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 7, 9);

    private sealed class Fixture
    {
        public InMemoryKillSwitchStore KillSwitch { get; } = new();
        public InMemoryPauseStore Pause { get; } = new();
        public InMemoryLockoutStore Lockout { get; } = new();
        public InMemoryStageGateStore StageGate { get; }
        public PortfolioState State { get; init; } = new() { Capital = 100_000m };

        public Fixture(TradingStage stage = TradingStage.Stage0Verification)
        {
            StageGate = new InMemoryStageGateStore(stage);
        }

        public RiskStatusView Build()
        {
            var builder = new PortfolioSnapshotBuilder(
                new FakePortfolioStateProvider(State), KillSwitch, Pause);
            var svc = new RiskStatusService(
                builder,
                new InMemoryRiskSettingsStore(),
                Pause,
                Lockout,
                StageGate,
                new FakeClock(Now, Today));
            return svc.Build();
        }
    }

    [Fact]
    public void 統制がすべて不成立なら新規建ては停止しない()
    {
        var status = new Fixture().Build();

        status.KillSwitchEngaged.Should().BeFalse();
        status.DailyLossLockoutActive.Should().BeFalse();
        status.TradingPaused.Should().BeFalse();
        status.ActiveControl.Should().Be(ActiveTradingControl.None);
        status.NewEntriesBlocked.Should().BeFalse();
    }

    [Fact]
    public void 一時停止のみ成立なら優先中の統制は一時停止で新規建て停止()
    {
        var f = new Fixture();
        f.Pause.SetState(new PauseState(true, "user", "様子見", Now));

        var status = f.Build();

        status.TradingPaused.Should().BeTrue();
        status.ActiveControl.Should().Be(ActiveTradingControl.Pause);
        status.NewEntriesBlocked.Should().BeTrue();
    }

    [Fact]
    public void 日次損失ロックアウトのみ成立なら優先中の統制はロックアウト()
    {
        var f = new Fixture();
        f.Lockout.Set(new LockoutState(Today.AddDays(1), "日次損失上限到達", Now));

        var status = f.Build();

        status.DailyLossLockoutActive.Should().BeTrue();
        status.LockoutReleaseOn.Should().Be(Today.AddDays(1));
        status.ActiveControl.Should().Be(ActiveTradingControl.DailyLossLockout);
        status.NewEntriesBlocked.Should().BeTrue();
    }

    [Fact]
    public void 複数成立時は優先順位に従いkill_switchを最優先で示す()
    {
        // ADR-0009: 優先順位 kill switch > 日次損失ロックアウト > 一時停止。3 つ同時成立でも表示は kill switch。
        var f = new Fixture();
        f.KillSwitch.SetState(new KillSwitchState(true, "user", "緊急停止", Now));
        f.Lockout.Set(new LockoutState(Today.AddDays(1), "日次損失上限到達", Now));
        f.Pause.SetState(new PauseState(true, "user", "様子見", Now));

        var status = f.Build();

        status.ActiveControl.Should().Be(ActiveTradingControl.KillSwitch);
        status.NewEntriesBlocked.Should().BeTrue();
    }

    [Fact]
    public void ロックアウトが解除日到達済みなら成立とみなさない()
    {
        // 表示専用: ReleaseOn == Today（当日が解除日）は IsActiveOn=false（当日以降は無効）。
        var f = new Fixture();
        f.Lockout.Set(new LockoutState(Today, "前営業日の損失上限", Now.AddDays(-1)));

        var status = f.Build();

        status.DailyLossLockoutActive.Should().BeFalse();
        status.ActiveControl.Should().Be(ActiveTradingControl.None);
    }

    [Fact]
    public void 段階と当日損益とポジションを反映する()
    {
        var f = new Fixture(TradingStage.Stage1Paper)
        {
            State = new PortfolioState
            {
                Capital = 100_000m,
                OpenPositionCount = 3,
                DailyRealizedPnl = -500m,
                UnrealizedPnl = -1_200m,
                DailyOrderedAmount = 40_000m,
                DrawdownRatio = 0.05m,
            },
        };

        var status = f.Build();

        status.Stage.Should().Be(TradingStage.Stage1Paper);
        status.DailyRealizedPnl.Should().Be(-500m);
        status.UnrealizedPnl.Should().Be(-1_200m);
        status.DailyPnl.Should().Be(-1_700m);
        status.OpenPositionCount.Should().Be(3);
        status.DailyOrderedAmount.Should().Be(40_000m);
        status.MaxDailyOrderAmount.Should().BeGreaterThan(0m);
        status.MaxOpenPositions.Should().BeGreaterThan(0);
    }
}
