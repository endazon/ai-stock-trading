using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Domain.Tests;

// FR-17: 全体前提条件（05_trading-assumptions §5）の既定値が適用されること
public class TradingDefaultsTests
{
    [Fact]
    public void リスク統制の既定値は全体前提条件と一致する()
    {
        var limits = TradingDefaults.CreateRiskLimits();

        limits.DailyLossLimitRatio.Should().Be(0.02m);   // 日次損失上限: 資金の 2%
        limits.PerTradeRiskRatio.Should().Be(0.01m);     // 1取引あたりリスク: 資金の 1%（0.5〜1% の上限側）
        limits.MaxDrawdownRatio.Should().Be(0.10m);      // 最大DD上限: 10%（10〜15% の保守側）
        limits.LosingStreakThreshold.Should().Be(3);     // 3連敗でサイズ半減（3〜5 の保守側）
        limits.LosingStreakSizeFactor.Should().Be(0.5m);
    }

    [Fact]
    public void 取引ガードの既定値は現物のみ有効かつ日米市場有効である()
    {
        var guard = TradingDefaults.CreateGuardSettings();

        guard.EnabledProductTypes.Should().BeEquivalentTo([ProductType.Cash]); // 信用は無効
        guard.EnabledMarkets.Should().BeEquivalentTo([Market.Japan, Market.UnitedStates]);
        guard.PreventSameDayReentry.Should().BeTrue();
    }

    [Fact]
    public void 取引禁止銘柄の既定値は利用者登録の3銘柄である()
    {
        var guard = TradingDefaults.CreateGuardSettings();

        guard.BannedSymbols.Select(b => b.Symbol)
            .Should().BeEquivalentTo(["6457", "6902", "6502"]);
        guard.BannedSymbols.Should().OnlyContain(b => !string.IsNullOrWhiteSpace(b.Reason));
    }

    [Fact]
    public void 運用段階の既定値はStage0のペーパーモードである()
    {
        var stage = TradingDefaults.CreateStageSettings();

        stage.Stage.Should().Be(TradingStage.Stage0Verification);
        stage.Mode.Should().Be(TradeMode.Paper);
        stage.CapitalCap.Should().Be(100_000m); // 初期投入資金（利用者決定 2026-07-07）
    }
}
