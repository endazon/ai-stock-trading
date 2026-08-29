using TradeDecisionService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Kernel.Trading;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-17, IADR-0076 決定1/3: 採算費用見積りアダプタの検証。
// 版付き前提条件（IAssumptionsProvider）＋既存の概算費用関数（CostCalculator）から往復費用・倍率を供給し、
// 未解決（IsResolved=false）なら null（＝採算見積り不能＝安全側 Hold）に倒すことを保証する。
public class AssumptionsProfitabilityProviderTests
{
    private sealed class FakeAssumptions(VersionedAssumptions value) : IAssumptionsProvider
    {
        public ValueTask<VersionedAssumptions> GetCurrentAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(value);
    }

    // 米国株の手数料 0.5%・為替スプレッド 0（本テストは費用算出の写像確認が目的）。
    private static TradingAssumptions Assumptions() => new()
    {
        CapitalGainsTaxRate = 0.20315m,
        JapanCommission = new CommissionSchedule(0m, 0m, 0m),
        UnitedStatesCommission = new CommissionSchedule(0.005m, 0m, 0m),
        FxSpreadRatio = 0m,
        MinimumExpectedProfitMultiple = 1.5m,
        CostLimits = new MonthlyCostLimits(20_000m, 15_000m, 5_000m, 0m),
    };

    [Fact]
    public async Task 解決済みなら往復費用と倍率と版を返す()
    {
        var provider = new AssumptionsProfitabilityProvider(
            new FakeAssumptions(new VersionedAssumptions(Assumptions(), Version: 7)));

        var assessment = await provider.AssessAsync(Market.UnitedStates, notional: 20_000m);

        assessment.Should().NotBeNull();
        // 片道 = 20,000 × 0.5% = 100。往復 = 200。
        assessment!.RoundTripCost.Should().Be(200m);
        assessment.MinimumProfitMultiple.Should().Be(1.5m);
        assessment.AssumptionsVersion.Should().Be(7);
    }

    [Fact]
    public async Task 未解決なら費用見積り不能でnullを返す()
    {
        // Version=0（UnresolvedVersion）＝既定値へのフェイルセーフ。実額未登録の手数料 0 でしきい値を緩めない（決定3）。
        var provider = new AssumptionsProfitabilityProvider(
            new FakeAssumptions(new VersionedAssumptions(
                TradingAssumptionsDefaults.Create(), VersionedAssumptions.UnresolvedVersion)));

        var assessment = await provider.AssessAsync(Market.UnitedStates, notional: 20_000m);

        assessment.Should().BeNull();
    }
}
