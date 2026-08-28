using System.Reflection;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace BacktestService.Domain.Tests;

// FR-15, FR-17, 06_daytrading-review §3.2: 現実的コスト計上（FR-17 費用関数＋スリッページ）と
// コスト2倍の感度分析を検証する。「コストを2倍にしても期待値が正」を合格条件にするための倍率適用。
public class BacktestCostModelTests
{
    // 手数料 0.1%（最低1）・為替スプレッド 0.2%・スリッページ 0.05%。
    // #364, IADR-0152 決定7: 為替スプレッドは**非基準通貨市場**（基準通貨 USD では日本市場）に掛かるため、
    // スプレッドを含む合算の検証は日本市場で行う。
    private static readonly TradingAssumptions Assumptions = TradingAssumptionsDefaults.Create() with
    {
        JapanCommission = new CommissionSchedule(Rate: 0.001m, Minimum: 1m, Cap: 0m),
        UnitedStatesCommission = new CommissionSchedule(Rate: 0.001m, Minimum: 1m, Cap: 0m),
        FxSpreadRatio = 0.002m,
    };

    private static readonly BacktestCostModel Model = new(Assumptions, SlippageRatio: 0.0005m);

    [Fact]
    public void 片道費用は手数料と為替スプレッドとスリッページの合算()
    {
        // 手数料 10000*0.001=10 ＋ 為替 10000*0.002=20 ＋ スリッページ 10000*0.0005=5 = 35。
        Model.OneWayCost(Market.Japan, 10_000m, CostSensitivity.Baseline)
            .Should().Be(35m);
    }

    [Fact]
    public void コスト2倍感度は片道費用を2倍にする()
    {
        Model.OneWayCost(Market.Japan, 10_000m, CostSensitivity.Doubled)
            .Should().Be(70m);
    }

    [Fact]
    public void 往復費用は片道の2倍()
    {
        Model.RoundTripCost(Market.Japan, 10_000m, CostSensitivity.Baseline)
            .Should().Be(70m);
    }

    // **否定形**: 基準通貨の市場（米国株）では通貨の交換が起こらないため為替スプレッドを課さない（IADR-0152 決定7）。
    [Fact]
    public void 基準通貨の市場は為替スプレッドを課さない()
    {
        // 手数料 10000*0.001=10 ＋ スリッページ 5 = 15（為替スプレッドなし）。
        Model.OneWayCost(Market.UnitedStates, 10_000m, CostSensitivity.Baseline).Should().Be(15m);
    }

    // T-15-69（**否定形**）: FR-15, FR-17, FR-10, ADR-0016 決定3（2026-08-06 改訂）, IADR-0158 決定3, #417 ——
    // **借株料をバックテストの費用モデルへ流し込まない。** moomoo の `ShortFeeRate`（実測 `1.5`）は
    // **単位が未確定**であり、取り違えると費用が 100 倍ずれて Stage 0 の合格判定（コスト 2 倍でも期待値が正）が
    // 意味を失う。ADR-0016 決定14 はバックテストへ借株料を織り込むと定めているが、**織り込む値の単位が
    // 決まっていない**ため、単位の裁定が下りるまで接続しない（本テストが接続を検知して赤くする）。
    [Fact]
    public void 借株料は費用モデルの入口に存在しない()
    {
        var surface = typeof(BacktestCostModel)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .SelectMany(member => new[] { member.Name }.Concat(
                member is MethodBase method
                    ? method.GetParameters().Select(p => p.Name ?? string.Empty)
                    : Enumerable.Empty<string>()))
            .ToList();

        surface.Should().NotContain(
            name => name.Contains("Borrow", StringComparison.OrdinalIgnoreCase)
                || name.Contains("ShortFee", StringComparison.OrdinalIgnoreCase),
            "借株料の単位が未確定である間は費用モデルへ接続してはならない（ADR-0016 決定3 の 2026-08-06 改訂）");
    }
}
