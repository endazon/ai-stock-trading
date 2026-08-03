using AiStockTrading.Configuration.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Backtest.Domain.Tests;

// FR-15, FR-17, 06_daytrading-review §3.2: 現実的コスト計上（FR-17 費用関数＋スリッページ）と
// コスト2倍の感度分析を検証する。「コストを2倍にしても期待値が正」を合格条件にするための倍率適用。
public class BacktestCostModelTests
{
    // 手数料 0.1%（最低1）・為替スプレッド 0.2%・スリッページ 0.05%。
    private static readonly TradingAssumptions Assumptions = TradingAssumptionsDefaults.Create() with
    {
        UnitedStatesCommission = new CommissionSchedule(Rate: 0.001m, Minimum: 1m, Cap: 0m),
        FxSpreadRatio = 0.002m,
    };

    private static readonly BacktestCostModel Model = new(Assumptions, SlippageRatio: 0.0005m);

    [Fact]
    public void 片道費用は手数料と為替スプレッドとスリッページの合算()
    {
        // 手数料 10000*0.001=10 ＋ 為替 10000*0.002=20 ＋ スリッページ 10000*0.0005=5 = 35。
        Model.OneWayCost(Market.UnitedStates, 10_000m, CostSensitivity.Baseline)
            .Should().Be(35m);
    }

    [Fact]
    public void コスト2倍感度は片道費用を2倍にする()
    {
        Model.OneWayCost(Market.UnitedStates, 10_000m, CostSensitivity.Doubled)
            .Should().Be(70m);
    }

    [Fact]
    public void 往復費用は片道の2倍()
    {
        Model.RoundTripCost(Market.UnitedStates, 10_000m, CostSensitivity.Baseline)
            .Should().Be(70m);
    }

    [Fact]
    public void 日本株は為替スプレッドを課さない()
    {
        // 手数料 10000*0.001=10 ＋ スリッページ 5 = 15（為替スプレッドなし）。
        var jpAssumptions = Assumptions with
        {
            JapanCommission = new CommissionSchedule(0.001m, 1m, 0m),
        };
        var jpModel = new BacktestCostModel(jpAssumptions, 0.0005m);
        jpModel.OneWayCost(Market.Japan, 10_000m, CostSensitivity.Baseline).Should().Be(15m);
    }
}
