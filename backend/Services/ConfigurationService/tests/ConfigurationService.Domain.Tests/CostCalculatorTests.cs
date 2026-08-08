using System.Reflection;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Configuration.Domain.Tests;

// FR-17, 05_trading-assumptions §4: 概算費用関数（手数料クランプ・為替スプレッド・往復・最小期待利益）を検証する。
public class CostCalculatorTests
{
    private static TradingAssumptions Assumptions(
        CommissionSchedule? jp = null, CommissionSchedule? us = null,
        decimal fxSpreadRatio = 0m, decimal minMultiple = 1.5m) =>
        new()
        {
            CapitalGainsTaxRate = 0.20315m,
            JapanCommission = jp ?? new CommissionSchedule(0m, 0m, 0m),
            UnitedStatesCommission = us ?? new CommissionSchedule(0m, 0m, 0m),
            FxSpreadRatio = fxSpreadRatio,
            MinimumExpectedProfitMultiple = minMultiple,
            CostLimits = new MonthlyCostLimits(20_000m, 15_000m, 5_000m, 0m),
        };

    [Fact]
    public void 手数料は定率で算出される()
    {
        var a = Assumptions(jp: new CommissionSchedule(0.001m, 0m, 0m));
        CostCalculator.EstimateOneWayCost(a, Market.Japan, 100_000m).Should().Be(100m); // 0.1%
    }

    [Fact]
    public void 手数料は最低額でフロアされる()
    {
        var a = Assumptions(jp: new CommissionSchedule(0.001m, 150m, 0m));
        CostCalculator.EstimateOneWayCost(a, Market.Japan, 100_000m).Should().Be(150m); // 100 < 最低150
    }

    [Fact]
    public void 手数料は上限でキャップされる()
    {
        var a = Assumptions(jp: new CommissionSchedule(0.001m, 0m, 50m));
        CostCalculator.EstimateOneWayCost(a, Market.Japan, 100_000m).Should().Be(50m); // 100 > 上限50
    }

    // FR-17, #364, IADR-0152 決定7: 為替スプレッドは通貨の交換に伴う費用であり、**非基準通貨市場**に掛かる。
    // 基準通貨が USD である現在、対象は日本市場である（旧実装は Market.Japan を直書きし JPY 基準に依存していた）。
    [Fact]
    public void 為替スプレッドは非基準通貨市場に約定代金比で加算される()
    {
        var a = Assumptions(jp: new CommissionSchedule(0m, 0m, 0m), fxSpreadRatio: 0.002m);
        // 手数料0 + 為替スプレッド 100,000*0.002 = 200。
        CostCalculator.EstimateOneWayCost(a, Market.Japan, 100_000m).Should().Be(200m);
    }

    // **否定形**: 基準通貨の市場では通貨の交換が起こらないため、為替スプレッドを課さない。
    [Fact]
    public void 基準通貨の市場には為替スプレッドを課さない()
    {
        var a = Assumptions(us: new CommissionSchedule(0m, 0m, 0m), fxSpreadRatio: 0.002m);

        MarketCurrency.IsBaseCurrency(Market.UnitedStates).Should().BeTrue();
        CostCalculator.EstimateOneWayCost(a, Market.UnitedStates, 100_000m).Should().Be(0m);
    }

    [Fact]
    public void 往復費用は片道の2倍()
    {
        var a = Assumptions(jp: new CommissionSchedule(0.001m, 0m, 0m));
        CostCalculator.EstimateRoundTripCost(a, Market.Japan, 100_000m).Should().Be(200m);
    }

    // FR-17, §4, #358, IADR-0173: しきい値の基準は **往復費用＋税** である（往復費用のみではない）。
    // 税は譲渡益（= 利益 − 費用）に掛かるため不動点で解く: T = m × C × (1 − r) / (1 − m × r)。
    [Fact]
    public void 最小期待利益は往復費用と税の合計の倍率倍()
    {
        var a = Assumptions(jp: new CommissionSchedule(0.001m, 0m, 0m), minMultiple: 2m);

        // 往復 200・m=2・r=0.20315 → T = 2 × 200 × 0.79685 / 0.5937 = 536.87…
        // **旧実装（往復費用のみ × 1.5 = 300）の約 1.79 倍**である。
        var expected = 2m * 200m * (1m - TradingAssumptionsDefaults.CapitalGainsTaxRate)
            / (1m - 2m * TradingAssumptionsDefaults.CapitalGainsTaxRate);

        CostCalculator.MinimumViableProfit(a, Market.Japan, 100_000m).Should().Be(expected);
        // 期待値そのものが式の写しにならないよう、桁の水準も併せて固定する（下 2 桁は decimal の除算に依存）。
        CostCalculator.MinimumViableProfit(a, Market.Japan, 100_000m).Should().BeApproximately(536.87m, 0.01m);
    }

    // 対照（退化）: 税率 0 なら従来式 m × C に一致する。式の書き換えが値を壊していないことを示す。
    [Fact]
    public void 税率0では従来式の往復費用の倍率倍へ退化する()
    {
        var a = Assumptions(jp: new CommissionSchedule(0.001m, 0m, 0m), minMultiple: 1.5m) with
        {
            CapitalGainsTaxRate = 0m,
        };

        CostCalculator.MinimumViableProfit(a, Market.Japan, 100_000m).Should().Be(300m);
    }

    // **fail-closed**: 倍率 × 税率 >= 1 では解が無い（利益を増やすと税も同じ速さで増える）。
    // 負のしきい値を返して全通過させないことを固定する。
    //
    // ⚠️ **期待を変更した（2026-08-08・#461・IADR-0177）。** 本テストは 2026-08-08 まで
    // 「`InvalidOperationException` を送出する」を固定していた。計画の裁定（planning#289・
    // 「解が無い領域では見送る」）が **3 経路の振る舞いを揃える**ことを定め、**例外は通過させない
    // 向きは合っていたが「見送り」とは壊れ方が違う**（処理ごと落ちる）ため、`null` へ変更した。
    // **最初からこうだったのではない。**
    [Fact]
    public void 倍率と税率の積が1以上なら解が無く見送りになる()
    {
        var a = Assumptions(jp: new CommissionSchedule(0.001m, 0m, 0m), minMultiple: 5m) with
        {
            CapitalGainsTaxRate = 0.20315m,   // 5 × 0.20315 = 1.01575 >= 1
        };

        CostCalculator.MinimumViableProfit(a, Market.Japan, 100_000m).Should().BeNull();
    }

    // T-17-01/02/03（#461, IADR-0177）: **境界そのもの**を固定する。上のテストは境界の「かなり内側」を
    // 突いているだけで、**不等号の向き（`>` か `>=` か）を取り違えても通ってしまう**。
    //
    // 境界は `倍率 × 税率 = 1`、すなわち `倍率 = 1 ÷ 税率`。税率 20.315% では ≈ 4.92247…。
    [Fact]
    public void 倍率と税率の積がちょうど1なら解が無く見送りになる()
    {
        const decimal Rate = 0.20315m;
        // 1 ÷ 0.20315 を decimal で丸めると積が 1 をわずかに割ることがあるため、
        // **積が 1 以上になる最小側**へ寄せた値を使う（丸めで検査が緩まないようにする）。
        var multiple = decimal.Ceiling(1m / Rate * 1_000_000m) / 1_000_000m;
        (multiple * Rate).Should().BeGreaterThanOrEqualTo(1m);   // 前提そのものを検査する

        var a = Assumptions(jp: new CommissionSchedule(0.001m, 0m, 0m), minMultiple: multiple) with
        {
            CapitalGainsTaxRate = Rate,
        };

        CostCalculator.MinimumViableProfit(a, Market.Japan, 100_000m).Should().BeNull();
    }

    [Fact]
    public void 境界の直前では正のしきい値が返る()
    {
        const decimal Rate = 0.20315m;
        // 積が 1 を下回る最大側へ寄せる。
        var multiple = decimal.Floor(1m / Rate * 1_000_000m) / 1_000_000m;
        (multiple * Rate).Should().BeLessThan(1m);               // 前提そのものを検査する

        var a = Assumptions(jp: new CommissionSchedule(0.001m, 0m, 0m), minMultiple: multiple) with
        {
            CapitalGainsTaxRate = Rate,
        };

        // **境界の直前でも「解がある」＝ null にならない。** 安全側へ倒しすぎて
        // 正常な構成まで見送るようになっていないことを固定する（fail-closed の行き過ぎ検知）。
        CostCalculator.MinimumViableProfit(a, Market.Japan, 100_000m)
            .Should().NotBeNull().And.BeGreaterThan(0m);
    }

    // T-17-04（#461, IADR-0177）: **負のしきい値は、いかなる入力でも返らない。**
    // 裁定の核心は「負のしきい値を返して全通過させることは、いかなる経路でも行わない」であり、
    // **値の一点ではなく全域**で固定する。
    [Theory]
    [InlineData(2, 0.20315)]      // 現行値
    [InlineData(4.9, 0.20315)]    // 境界の手前
    [InlineData(5, 0.20315)]      // 境界の先（解無し）
    [InlineData(100, 0.5)]        // 大きく踏み越えた構成異常
    [InlineData(2, 0)]            // 税率 0（従来式へ退化）
    [InlineData(1, 0.99)]         // 税率が極端
    public void しきい値は負にならない(double multiple, double taxRate)
    {
        var a = Assumptions(jp: new CommissionSchedule(0.001m, 0m, 0m), minMultiple: (decimal)multiple) with
        {
            CapitalGainsTaxRate = (decimal)taxRate,
        };

        var threshold = CostCalculator.MinimumViableProfit(a, Market.Japan, 100_000m);

        // null（見送り）であるか、非負であるかのいずれか。**負の値は許さない。**
        if (threshold is { } value)
        {
            value.Should().BeGreaterThanOrEqualTo(0m);
        }
    }

    // T-10-214（**否定形**）: FR-17, FR-10, ADR-0016 決定3（2026-08-06 改訂）, IADR-0158 決定3, #417 ——
    // **借株料を費用計算へ流し込まない。** moomoo の `ShortFeeRate`（実測 `1.5`）は**単位が未確定**であり
    //（年率 1.5% か比率 1.5 か）、取り違えると費用モデルが 100 倍ずれて採算判定（最小期待利益）が丸ごと狂う。
    // 値ではなく**構造**で固定する——借株料の入口が公開面に生えた時点で赤くなり、単位が確定しないまま
    // 接続することを「気づかないうちに」許さない（接続してよいのは単位の裁定が下りた後である）。
    [Fact]
    public void 借株料は費用計算の入口に存在しない()
    {
        var surface = PublicSurfaceNames(typeof(CostCalculator))
            .Concat(PublicSurfaceNames(typeof(TradingAssumptions)))
            .ToList();

        surface.Should().NotContain(
            name => name.Contains("Borrow", StringComparison.OrdinalIgnoreCase)
                || name.Contains("ShortFee", StringComparison.OrdinalIgnoreCase),
            "借株料の単位が未確定である間は FR-17 の費用計算へ接続してはならない（ADR-0016 決定3 の 2026-08-06 改訂）");
    }

    // 型の公開面（メンバ名 ＋ メソッド・コンストラクタの引数名）を列挙する。
    private static IEnumerable<string> PublicSurfaceNames(Type type) =>
        type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .SelectMany(member => new[] { member.Name }.Concat(
                member is MethodBase method
                    ? method.GetParameters().Select(p => p.Name ?? string.Empty)
                    : Enumerable.Empty<string>()));
}
