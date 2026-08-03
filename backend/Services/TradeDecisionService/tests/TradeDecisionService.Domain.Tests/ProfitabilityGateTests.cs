using AiStockTrading.TradeDecision.Domain;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.TradeDecision.Domain.Tests;

// FR-17, 05_trading-assumptions §4, IADR-0076: 採算評価ゲート（純関数）のテスト。
// しきい値 = (往復費用 + 判断費用) × 最小期待利益倍率。想定利益 ≥ しきい値 → Viable。
// 費用見積り不能（往復費用 null/≤0・倍率≤0）は Indeterminate（呼び出し側で Hold＝安全側）。
public class ProfitabilityGateTests
{
    // 基準1: 想定利益がしきい値以上なら Viable。
    [Fact]
    public void 想定利益がしきい値以上なら採算成立()
    {
        // 往復費用 100 + 判断費用 0、倍率 1.5 → しきい値 150。想定利益 150 は成立。
        var verdict = ProfitabilityGate.Evaluate(
            expectedGrossProfit: 150m, estimatedRoundTripCost: 100m, decisionCost: 0m, minimumProfitMultiple: 1.5m);

        verdict.Should().Be(ProfitabilityVerdict.Viable);
    }

    // 基準1: しきい値ちょうどは境界を採算成立に含める（>=）。
    [Fact]
    public void しきい値ちょうどは採算成立に含める()
    {
        var verdict = ProfitabilityGate.Evaluate(
            expectedGrossProfit: 300m, estimatedRoundTripCost: 100m, decisionCost: 0m, minimumProfitMultiple: 3m);

        verdict.Should().Be(ProfitabilityVerdict.Viable);
    }

    // 基準1: 想定利益がしきい値未満なら NotViable。
    [Fact]
    public void 想定利益がしきい値未満なら採算不成立()
    {
        // しきい値 150、想定利益 149.99 は不成立。
        var verdict = ProfitabilityGate.Evaluate(
            expectedGrossProfit: 149.99m, estimatedRoundTripCost: 100m, decisionCost: 0m, minimumProfitMultiple: 1.5m);

        verdict.Should().Be(ProfitabilityVerdict.NotViable);
    }

    // 基準1: 判断費用も往復費用に加算してしきい値を上げる。
    [Fact]
    public void 判断費用はしきい値に加算される()
    {
        // (往復100 + 判断20) × 1.5 = 180。想定利益 170 は不成立、190 は成立。
        ProfitabilityGate.Evaluate(170m, 100m, 20m, 1.5m).Should().Be(ProfitabilityVerdict.NotViable);
        ProfitabilityGate.Evaluate(190m, 100m, 20m, 1.5m).Should().Be(ProfitabilityVerdict.Viable);
    }

    // 基準2: 往復費用 null（前提条件が未解決）は Indeterminate（費用見積り不能→安全側）。
    [Fact]
    public void 往復費用がnullなら見積り不能()
    {
        var verdict = ProfitabilityGate.Evaluate(
            expectedGrossProfit: 10_000m, estimatedRoundTripCost: null, decisionCost: 0m, minimumProfitMultiple: 1.5m);

        verdict.Should().Be(ProfitabilityVerdict.Indeterminate);
    }

    // 基準2: 往復費用 0（moomoo 実額未登録）は Indeterminate。費用 0 でしきい値 0＝全通過を許さない。
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 往復費用が非正なら見積り不能(double roundTrip)
    {
        var verdict = ProfitabilityGate.Evaluate(
            expectedGrossProfit: 10_000m, estimatedRoundTripCost: (decimal)roundTrip, decisionCost: 0m, minimumProfitMultiple: 1.5m);

        verdict.Should().Be(ProfitabilityVerdict.Indeterminate);
    }

    // 基準2: 倍率が非正（構成異常）は Indeterminate。
    [Theory]
    [InlineData(0)]
    [InlineData(-1.5)]
    public void 倍率が非正なら見積り不能(double multiple)
    {
        var verdict = ProfitabilityGate.Evaluate(
            expectedGrossProfit: 10_000m, estimatedRoundTripCost: 100m, decisionCost: 0m, minimumProfitMultiple: (decimal)multiple);

        verdict.Should().Be(ProfitabilityVerdict.Indeterminate);
    }

    // 基準3: 判断費用の負値は 0 に正規化する（負の費用でしきい値を下げない）。
    [Fact]
    public void 判断費用の負値は0に正規化する()
    {
        // 負の判断費用でも (往復100 + 0) × 1.5 = 150 のまま。想定利益 150 は成立、149 は不成立。
        ProfitabilityGate.Evaluate(150m, 100m, -50m, 1.5m).Should().Be(ProfitabilityVerdict.Viable);
        ProfitabilityGate.Evaluate(149m, 100m, -50m, 1.5m).Should().Be(ProfitabilityVerdict.NotViable);
    }

    // 想定利益 0（LLM が想定利益を示さない）＋正のしきい値は不成立（保守側）。
    [Fact]
    public void 想定利益ゼロは採算不成立()
    {
        ProfitabilityGate.Evaluate(0m, 100m, 0m, 1.5m).Should().Be(ProfitabilityVerdict.NotViable);
    }
}
