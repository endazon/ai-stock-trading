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

    [Fact]
    public void 最小期待利益は往復費用の倍率倍()
    {
        var a = Assumptions(jp: new CommissionSchedule(0.001m, 0m, 0m), minMultiple: 1.5m);
        // 往復200 × 1.5 = 300。
        CostCalculator.MinimumViableProfit(a, Market.Japan, 100_000m).Should().Be(300m);
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
