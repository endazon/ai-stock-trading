using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// FR-17, FR-10, 05_trading-assumptions §4, #358, #461, IADR-0173, IADR-0177:
// **式の単一情報源**（MinimumExpectedProfit.Threshold）そのものを直接固定する。
//
// ⚠️ **本ファイルは 2026-08-08（#461）に新設した。** それまで単一情報源には**直接のテストが無く**、
// 呼び出し側 2 経路（CostCalculator / ProfitabilityGate）のテスト越しにしか検査されていなかった。
// **「3 経路が同じ意味を返す」ことを求めるなら、基準となる 1 経路が最も強く固定されていなければ
// ならない** —— 間接検査だけでは、呼び出し側が偶然同じ結論へ落ちている場合を区別できない。
public class MinimumExpectedProfitTests
{
    // 計画確定値（05_trading-assumptions §4）。
    private const decimal PlanTaxRate = 0.20315m;
    private const decimal PlanMultiple = 2m;

    // 基準: しきい値は **往復費用＋税** の m 倍を解いた不動点 T = m × C × (1 − r) / (1 − m × r)。
    [Fact]
    public void 計画確定値では費用の約2684倍になる()
    {
        var threshold = MinimumExpectedProfit.Threshold(cost: 100m, PlanMultiple, PlanTaxRate);

        // 式の写しにならないよう、桁の水準で固定する（2 × 100 × 0.79685 / 0.5937 = 268.4…）。
        threshold.Should().NotBeNull().And.BeApproximately(268.4m, 0.1m);
    }

    // 対照（退化）: 税率 0 では (1 − r) / (1 − m × r) = 1 となり従来式 m × C へ戻る。
    [Fact]
    public void 税率0では従来式へ退化する()
    {
        MinimumExpectedProfit.Threshold(cost: 100m, multiple: 1.5m, taxRate: 0m).Should().Be(150m);
    }

    // **裁定（planning#289・2026-08-08「解が無い領域では見送る」）の核心**:
    // 倍率 × 税率 >= 1 では解が無い。**null（＝見送り）を返し、負のしきい値は返さない。**
    // 属性引数に decimal は置けない（CS0182）ため double で受けて変換する。
    [Theory]
    [InlineData(5.0, 0.20315)]    // 1.01575（境界の先）
    [InlineData(2.0, 0.5)]        // 1.0 ちょうど —— **境界も解無しとして扱う**
    [InlineData(10.0, 0.9)]       // 9.0（大きく踏み越えた構成異常）
    [InlineData(1.0, 1.0)]        // 税率 100%
    public void 倍率と税率の積が1以上なら解が無い(double multiple, double taxRate)
    {
        MinimumExpectedProfit.Threshold(100m, (decimal)multiple, (decimal)taxRate).Should().BeNull();
    }

    // **境界の直前は解を持つ。** 安全側へ倒しすぎて正常な構成まで見送っていないことを固定する
    //（fail-closed の行き過ぎ検知）。上のテストだけでは不等号を `>=` から `>` へ広げても気付けない。
    [Fact]
    public void 境界の直前では正のしきい値が返る()
    {
        // 積が 1 を下回る最大側へ寄せる（1 ÷ 0.20315 ≈ 4.92247…）。
        var multiple = decimal.Floor(1m / PlanTaxRate * 1_000_000m) / 1_000_000m;
        (multiple * PlanTaxRate).Should().BeLessThan(1m);   // 前提そのものを検査する

        MinimumExpectedProfit.Threshold(100m, multiple, PlanTaxRate)
            .Should().NotBeNull().And.BeGreaterThan(0m);
    }

    // **負のしきい値は、いかなる入力でも返らない。** 裁定は「負のしきい値を返して全通過させることは、
    // いかなる経路でも行わない」と定める。値の一点ではなく**全域**で固定する。
    [Theory]
    [InlineData(100.0, 2.0, 0.20315)]
    [InlineData(100.0, 4.9, 0.20315)]
    [InlineData(100.0, 5.0, 0.20315)]
    [InlineData(100.0, 100.0, 0.5)]
    [InlineData(100.0, 2.0, 0.0)]
    [InlineData(0.0, 2.0, 0.20315)]
    [InlineData(-100.0, 2.0, 0.20315)]    // 負の費用（正規化される）
    [InlineData(100.0, 2.0, -0.5)]        // 負の税率（正規化される）
    [InlineData(100.0, -2.0, 0.20315)]    // 負の倍率（構成異常）
    public void しきい値は負にならない(double cost, double multiple, double taxRate)
    {
        var threshold = MinimumExpectedProfit.Threshold(
            (decimal)cost, (decimal)multiple, (decimal)taxRate);

        // null（見送り）であるか、非負であるかのいずれか。**負の値は許さない。**
        if (threshold is { } value)
        {
            value.Should().BeGreaterThanOrEqualTo(0m);
        }
    }

    // 正規化: 負の費用・負の税率は 0 として扱う（XML ドキュメントの契約）。
    [Fact]
    public void 負の費用と負の税率は0へ正規化される()
    {
        MinimumExpectedProfit.Threshold(cost: -100m, multiple: 2m, taxRate: 0.20315m).Should().Be(0m);
        MinimumExpectedProfit.Threshold(cost: 100m, multiple: 2m, taxRate: -0.5m).Should().Be(200m);
    }

    // 🔴 **回帰テスト（#461 で本テストが実際に見つけた欠陥）。**
    //
    // 費用と税率は正規化されるため、**負のしきい値を生み得るのは倍率だけ**である。しかも負の倍率では
    // 分母 `1 − m × r` が **1 より大きくなり、解無し判定（分母 <= 0）をすり抜ける**
    //（m = −2 / r = 0.20315 → 分母 1.406）。結果は**負のしきい値＝全通過**であった。
    //
    // 採算ゲート（ProfitabilityGate）は呼び出し前に `m <= 0` を弾いていたため露見せず、
    // **費用算出だけが素通ししていた** —— 呼び出し側ごとの防御は、片方だけ抜ける。
    // 裁定（planning#289）の「**いかなる経路でも**負のしきい値を返さない」に反する状態である。
    [Theory]
    [InlineData(-2.0)]
    [InlineData(-0.0001)]
    [InlineData(0.0)]
    public void 倍率が非正なら解無しに倒す_負のしきい値を生まない(double multiple)
    {
        MinimumExpectedProfit.Threshold(100m, (decimal)multiple, PlanTaxRate).Should().BeNull();
    }
}
