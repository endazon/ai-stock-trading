using RiskManagementService.Application.Adapters;
using RiskManagementService.Application.Services;
using RiskManagementService.Application.State;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Application.Tests;

// FR-04, FR-10, IADR-0029: サイジング文脈の導出（段階/日次残枠のクランプ・状態/設定由来）を検証する。
public class SizingContextServiceTests
{
    private static SizingContextService Build(PortfolioState state)
    {
        var snapshotBuilder = new PortfolioSnapshotBuilder(
            new FakePortfolioStateProvider(state), new InMemoryKillSwitchStore(), new InMemoryPauseStore(),
            // サイジング文脈は口座種別に依存しない（#375 は発注審査側の統制である）。
            FakeBrokerAccountObservations.NotObserved(), new InMemoryInformationDegradationStore());
        return new SizingContextService(snapshotBuilder, new InMemoryRiskSettingsStore());
    }

    [Fact]
    public void 段階_日次残枠は上限から使用分を引いて導出する()
    {
        // 既定: Stage 0 の発注可能額は総資金の 100%（#333・IADR-0136。段階としての絞りは無い）/
        // 日次上限は equity 比 150%（#329・計画 §5）。いずれも equity（Capital）から解決される。
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
        view.StageCapitalRemaining.Should().Be(100_000m - 40_000m); // 100,000 × 100% − 40,000
        // FR-10, #329: 日次上限は equity 比 150%。100,000 × 1.5 − 50,000 = 100,000。
        view.DailyOrderRemaining.Should().Be(100_000m);
        view.ConsecutiveLosses.Should().Be(2);
        view.DrawdownRatio.Should().Be(0.05m);
        view.Mode.Should().Be(BrokerProvider.InternalPaper);          // Stage0 は Paper
        view.Limits.MaxOrderAmountRatio.Should().Be(TradingDefaults.CreateRiskLimits().MaxOrderAmountRatio);
    }

    [Fact]
    public void 使用分が上限を超えると残枠は0にクランプされる()
    {
        var state = new PortfolioState
        {
            Capital = 100_000m,
            InvestedCapital = 100_001m, // 段階の発注可能額（100,000 × 100%）超過
            DailyOrderedAmount = 200_000m, // 日次上限（100,000 × 150% = 150,000）超過
        };

        var view = Build(state).Build();

        view.StageCapitalRemaining.Should().Be(0m);
        view.DailyOrderRemaining.Should().Be(0m);
    }
}
