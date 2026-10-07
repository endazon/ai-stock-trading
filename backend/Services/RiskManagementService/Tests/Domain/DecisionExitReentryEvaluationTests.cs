using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// T-10-2299, FR-10, #1176, IADR-0495 決定3: 判定コア（RiskEvaluator）が「当日の判断由来の決済（利確・判断の手仕舞い）」の供給で
// 同じ方向の新規建てを DecisionExitSameDay で止めること（StopOutReentryEvaluationTests と同じ形）。供給の組み立ては
// DecisionExitProjectionTests、Program.cs の実構成は DecisionExitReentryWiringTests が見る。
public class DecisionExitReentryEvaluationTests
{
    private static OrderIntent Intent(TradeSide side, PositionEffect effect, ProductType productType = ProductType.Cash) =>
        new("AMZN", Market.UnitedStates, side, productType, BrokerProvider.InternalPaper, 10, 255.63m, effect);

    private static PortfolioSnapshot Snapshot() => new() { Capital = 100_000m };

    private static RiskManagementSettings ShortEnabledSettings() => TradingDefaults.CreateSettings() with
    {
        Guard = TradingDefaults.CreateGuardSettings() with
        {
            EnabledProductTypes = new HashSet<ProductType> { ProductType.Cash, ProductType.MarginLong, ProductType.ShortSell },
        },
        Stage = new StageSettings(TradingStage.Stage3ScaledLive, BrokerProvider.InternalPaper, CapitalCapRatio: 1_000_000m),
    };

    private static OrderScreeningResult Evaluate(OrderIntent intent, DecisionExitReentrySupply? exits, RiskManagementSettings? settings = null) =>
        RiskEvaluator.Evaluate(intent, settings ?? TradingDefaults.CreateSettings(), Snapshot(), decisionExits: exits);

    [Fact]
    public void T_10_2293_ロングを判断で決済した当日の買いの新規建ては拒否される()
    {
        var result = Evaluate(Intent(TradeSide.Buy, PositionEffect.Open), new DecisionExitReentrySupply(true, false));

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().ContainSingle().Which.Should().Be(RejectionReason.DecisionExitSameDay);
    }

    [Fact]
    public void T_10_2293_ロングの利確は反対方向の新規建てを止めない()
    {
        var shortEntry = Intent(TradeSide.Sell, PositionEffect.Open, ProductType.ShortSell);

        var result = Evaluate(shortEntry, new DecisionExitReentrySupply(true, false), ShortEnabledSettings());

        result.Reasons.Should().NotContain(RejectionReason.DecisionExitSameDay);
    }

    [Fact]
    public void T_10_2293_ショートを判断で決済した当日は売りの新規建てだけを止める()
    {
        var supply = new DecisionExitReentrySupply(false, true);

        Evaluate(Intent(TradeSide.Sell, PositionEffect.Open, ProductType.ShortSell), supply, ShortEnabledSettings())
            .Reasons.Should().Contain(RejectionReason.DecisionExitSameDay);
        Evaluate(Intent(TradeSide.Buy, PositionEffect.Open), supply).IsApproved.Should().BeTrue();
    }

    // 🔴 手仕舞い（Close）は止めない（ADR-0009）。供給が無い（null＝この呼び出し元は供給していない）なら評価しない。
    [Fact]
    public void T_10_2293_手仕舞いは止めず供給が無ければ評価しない()
    {
        var both = new DecisionExitReentrySupply(true, true);

        Evaluate(Intent(TradeSide.Sell, PositionEffect.Close), both).IsApproved.Should().BeTrue();
        Evaluate(Intent(TradeSide.Buy, PositionEffect.Close), both).IsApproved.Should().BeTrue();
        Evaluate(Intent(TradeSide.Buy, PositionEffect.Open), null).IsApproved.Should().BeTrue();
        Evaluate(Intent(TradeSide.Buy, PositionEffect.Open), DecisionExitReentrySupply.NoneToday).IsApproved.Should().BeTrue();
    }

    // 損切りとは別の理由である（損切りの供給だけでは DecisionExitSameDay は立たず、両方なら両方の名前）。
    [Fact]
    public void T_10_2295_損切りの統制とは別の理由で立つ()
    {
        var stopped = new StopOutReentrySupply(StopOutStatus.StoppedOut, StopOutStatus.None);

        RiskEvaluator.Evaluate(Intent(TradeSide.Buy, PositionEffect.Open), TradingDefaults.CreateSettings(), Snapshot(),
                stopOuts: stopped, decisionExits: DecisionExitReentrySupply.NoneToday)
            .Reasons.Should().Equal(RejectionReason.StoppedOutSameDay);
        RiskEvaluator.Evaluate(Intent(TradeSide.Buy, PositionEffect.Open), TradingDefaults.CreateSettings(), Snapshot(),
                stopOuts: stopped, decisionExits: new DecisionExitReentrySupply(true, false))
            .Reasons.Should().Equal(RejectionReason.StoppedOutSameDay, RejectionReason.DecisionExitSameDay);
    }
}
