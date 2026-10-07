using TradeDecisionService.Domain;
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

        var assessment = await provider.AssessAsync(Market.UnitedStates, quantity: 100, notional: 20_000m);

        assessment.Should().NotBeNull();
        // 片道の手数料 = 20,000 × 0.5% = 100。往復 = 200 ＋ 売り 1 回分の取引諸費用（SEC 0.412 ＋ TAF 0.0166）。
        // ⚠️ 期待を変更した（2026-10-08・#1217・IADR-0508 決定1）: 2026-10-08 までは 200（諸費用を含まない）だった。
        // 計画 §4 の事前見積り「手数料＋諸費用＋為替スプレッド相当」へ揃えたためで、弱めたのではない。
        assessment!.RoundTripCost.Should().Be(200m + 0.412m + 0.0166m);
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

        var assessment = await provider.AssessAsync(Market.UnitedStates, quantity: 100, notional: 20_000m);

        assessment.Should().BeNull();
    }

    // T-17-10（**否定形**）: FR-17, IADR-0076 決定3, #1217, IADR-0508 決定2 ——
    // **手数料・為替スプレッドが未登録（0）なら、諸費用（計画の暫定値で常に埋まる）があっても見積り不能（null＝見送り）。**
    // 諸費用だけで往復費用が正になると、しきい値がほぼ 0 へ緩み「費用 0 で判定を緩めない」が破れる。
    [Fact]
    public async Task 登録費用が0なら取引諸費用があっても見積り不能でnullを返す()
    {
        var provider = new AssumptionsProfitabilityProvider(
            new FakeAssumptions(new VersionedAssumptions(TradingAssumptionsDefaults.Create(), Version: 4)));

        // 前提: 諸費用そのものは正（米国株の売りが往復に含まれる）。
        CostCalculator.EstimateRoundTripCostBreakdown(TradingAssumptionsDefaults.Create(), Market.UnitedStates, 100, 20_000m)
            .RegulatoryFees.Should().BeGreaterThan(0m);

        var assessment = await provider.AssessAsync(Market.UnitedStates, quantity: 100, notional: 20_000m);

        assessment.Should().BeNull();
    }

    // T-17-11: FR-17, 計画 ADR-0035 決定 5, #1217, IADR-0508 決定1 —— **変わる判断を固定する。**
    // 諸費用を含めない往復費用（手数料 200）のしきい値ちょうどの想定利益は、従来は通過（Viable）したが、
    // 諸費用を含めた往復費用ではしきい値に届かず見送り（NotViable）になる。米国株の売りの費用ぶん見送りの閾値が上がる。
    [Fact]
    public async Task 取引諸費用を含めると旧しきい値ちょうどの想定利益は見送りになる()
    {
        var provider = new AssumptionsProfitabilityProvider(
            new FakeAssumptions(new VersionedAssumptions(Assumptions(), Version: 7)));
        var assessment = (await provider.AssessAsync(Market.UnitedStates, quantity: 100, notional: 20_000m))!;

        var oldThreshold = MinimumExpectedProfit.Threshold(200m, 1.5m, 0.20315m)!.Value;

        // 対照: 諸費用を含めない往復費用（200）なら旧しきい値ちょうどで通過する（不等号は >=）。
        ProfitabilityGate.Evaluate(oldThreshold, 200m, 0m, 1.5m, 0.20315m).Should().Be(ProfitabilityVerdict.Viable);
        // 諸費用を含めた往復費用では同じ想定利益が見送りになる。
        ProfitabilityGate.Evaluate(
                oldThreshold, assessment.RoundTripCost, 0m, assessment.MinimumProfitMultiple, assessment.CapitalGainsTaxRate)
            .Should().Be(ProfitabilityVerdict.NotViable);
    }

    // 日本株の往復には諸費用が入らない（判断は変わらない）。
    [Fact]
    public async Task 日本株の往復費用には取引諸費用が入らない()
    {
        var provider = new AssumptionsProfitabilityProvider(new FakeAssumptions(new VersionedAssumptions(
            Assumptions() with { JapanCommission = new CommissionSchedule(0.001m, 0m, 0m) }, Version: 7)));

        var assessment = await provider.AssessAsync(Market.Japan, quantity: 100, notional: 100_000m);

        assessment!.RoundTripCost.Should().Be(200m);
    }
}
