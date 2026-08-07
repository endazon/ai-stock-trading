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

    // --- FR-17, §4, #358, IADR-0173: しきい値の基準は「往復費用＋**税**」である ---

    // **本ゲートは緩む方向に壊れても何も赤くならない**（発注が増えるだけである）。
    // したがって税込みしきい値の**境界を両側で**固定する。
    // 往復1000 + 判断0・m=2・r=0.20315 → T = 2 × 1000 × 0.79685 / 0.5937 = 2684.35…
    [Fact]
    public void 税込みしきい値の境界は両側で効く()
    {
        const decimal rate = 0.20315m;
        var threshold = 2m * 1000m * (1m - rate) / (1m - 2m * rate);

        ProfitabilityGate.Evaluate(threshold, 1000m, 0m, 2m, rate).Should().Be(ProfitabilityVerdict.Viable);
        ProfitabilityGate.Evaluate(threshold - 0.01m, 1000m, 0m, 2m, rate)
            .Should().Be(ProfitabilityVerdict.NotViable);
    }

    // **旧実装との差そのものを固定する。** 旧: 往復費用のみ × 1.5 = 1500。
    // 新: 往復費用＋税 × 2 ≒ 2684.35。**1500〜2684 の想定利益は、旧実装では通り新実装では通らない。**
    // この帯が消えると「税を無視する実装へ戻った」ことを意味する。
    [Fact]
    public void 旧実装なら通っていた想定利益が税込み基準では通らない()
    {
        ProfitabilityGate.Evaluate(1500m, 1000m, 0m, 2m, 0.20315m).Should().Be(ProfitabilityVerdict.NotViable);
        ProfitabilityGate.Evaluate(2000m, 1000m, 0m, 2m, 0.20315m).Should().Be(ProfitabilityVerdict.NotViable);
        ProfitabilityGate.Evaluate(2685m, 1000m, 0m, 2m, 0.20315m).Should().Be(ProfitabilityVerdict.Viable);
    }

    // 対照（退化）: 税率 0 なら従来式（費用 × 倍率）に一致する。
    [Fact]
    public void 税率0では従来のしきい値へ退化する()
    {
        ProfitabilityGate.Evaluate(150m, 100m, 0m, 1.5m, 0m).Should().Be(ProfitabilityVerdict.Viable);
        ProfitabilityGate.Evaluate(149m, 100m, 0m, 1.5m, 0m).Should().Be(ProfitabilityVerdict.NotViable);
    }

    // **fail-closed（本変更で最も重要）**: 倍率 × 税率 >= 1 では解が無い
    //（利益を増やすと税も同じ速さで増え、要求が伸び続ける）。
    // **負のしきい値を返して全通過させてはならない。** 構成異常として Indeterminate（見送り）へ倒す。
    // 属性引数に decimal は置けない（CS0182）ため double で受けて decimal へ変換する。
    [Theory]
    [InlineData(5.0, 0.20315)]    // 1.01575
    [InlineData(2.0, 0.5)]        // 1.0（ちょうど。境界も解なしとして扱う）
    [InlineData(10.0, 0.9)]       // 9.0
    public void 倍率と税率の積が1以上なら採算不能へ倒す(double multiple, double taxRate)
    {
        // 極端に大きい想定利益でも通さない（負のしきい値なら Viable になってしまう）。
        ProfitabilityGate.Evaluate(1_000_000_000m, 1000m, 0m, (decimal)multiple, (decimal)taxRate)
            .Should().Be(ProfitabilityVerdict.Indeterminate);
    }
}
