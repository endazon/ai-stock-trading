using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-04, FR-10, IADR-0029: サイジング文脈の導出（段階/日次残枠のクランプ・状態/設定由来）を検証する。
public class SizingContextServiceTests
{
    private static SizingContextService Build(PortfolioState state)
    {
        var snapshotBuilder = new PortfolioSnapshotBuilder(
            new FakePortfolioStateProvider(state), new InMemoryKillSwitchStore(), new InMemoryPauseStore());
        return new SizingContextService(snapshotBuilder, new InMemoryRiskSettingsStore());
    }

    [Fact]
    public void 段階_日次残枠は上限から使用分を引いて導出する()
    {
        // 既定: Stage.CapitalCap=100,000 / MaxDailyOrderAmount=100,000。
        var state = new PortfolioState
        {
            Capital = 100_000m,
            InvestedCapital = 40_000m,
            DailyOrderedAmount = 50_000m,
            ConsecutiveLosses = 2,
            DrawdownRatio = 0.05m,
        };

        var view = Build(state).Build();

        view.Capital.Should().Be(100_000m);
        view.StageCapitalRemaining.Should().Be(60_000m); // 100,000 − 40,000
        view.DailyOrderRemaining.Should().Be(50_000m);   // 100,000 − 50,000
        view.ConsecutiveLosses.Should().Be(2);
        view.DrawdownRatio.Should().Be(0.05m);
        view.Mode.Should().Be(TradeMode.Paper);          // Stage0 は Paper
        view.Limits.MaxOrderAmount.Should().Be(TradingDefaults.CreateRiskLimits().MaxOrderAmount);
    }

    [Fact]
    public void 使用分が上限を超えると残枠は0にクランプされる()
    {
        var state = new PortfolioState
        {
            Capital = 100_000m,
            InvestedCapital = 150_000m,   // CapitalCap 超過
            DailyOrderedAmount = 120_000m, // 日次上限超過
        };

        var view = Build(state).Build();

        view.StageCapitalRemaining.Should().Be(0m);
        view.DailyOrderRemaining.Should().Be(0m);
    }
}
