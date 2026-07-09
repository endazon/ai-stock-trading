using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-10, IADR-0005, IADR-0008: スナップショット構築の検証。生の運用状態と kill switch を合成し、
// InvestedCapital・UnrealizedPnl・KillSwitchEngaged を判定入力へ正しく反映する。
public class PortfolioSnapshotBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 運用状態の各値をスナップショットへ反映する()
    {
        var state = new PortfolioState
        {
            Capital = 100_000m,
            OpenPositionCount = 2,
            InvestedCapital = 40_000m,
            DailyOrderedAmount = 50_000m,
            DailyRealizedPnl = -500m,
            UnrealizedPnl = -1_200m,
            DrawdownRatio = 0.08m,
            ConsecutiveLosses = 2,
            SymbolsTradedToday = new HashSet<(string, Market)> { ("AAPL", Market.UnitedStates) },
        };
        var builder = new PortfolioSnapshotBuilder(new FakePortfolioStateProvider(state), new InMemoryKillSwitchStore());

        var snapshot = builder.Build();

        snapshot.Capital.Should().Be(100_000m);
        snapshot.OpenPositionCount.Should().Be(2);
        snapshot.InvestedCapital.Should().Be(40_000m);
        snapshot.DailyOrderedAmount.Should().Be(50_000m);
        snapshot.DailyRealizedPnl.Should().Be(-500m);
        snapshot.UnrealizedPnl.Should().Be(-1_200m);
        snapshot.DrawdownRatio.Should().Be(0.08m);
        snapshot.ConsecutiveLosses.Should().Be(2);
        snapshot.SymbolsTradedToday.Should().Contain(("AAPL", Market.UnitedStates));
        snapshot.KillSwitchEngaged.Should().BeFalse();
    }

    [Fact]
    public void kill_switch_の状態をスナップショットへ反映する()
    {
        var state = new PortfolioState { Capital = 100_000m };
        var killSwitch = new InMemoryKillSwitchStore();
        killSwitch.SetState(new KillSwitchState(true, "user", "停止", Now));
        var builder = new PortfolioSnapshotBuilder(new FakePortfolioStateProvider(state), killSwitch);

        builder.Build().KillSwitchEngaged.Should().BeTrue();
    }
}
