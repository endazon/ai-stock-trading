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
        guard.ProhibitManipulativeOrderPatterns.Should().BeTrue(); // FR-19: 発注パターン禁止は既定で有効
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
    public void 相場操縦検知の既定しきい値はIADR0040の初期値と一致する()
    {
        // FR-19, IADR-0040: 検知アルゴリズムの既定しきい値を固定する（運用データによる較正はフォローアップ）。
        var settings = TradingDefaults.CreateManipulationDetectionSettings();

        settings.LookbackWindow.Should().Be(TimeSpan.FromMinutes(5));
        settings.MinimumSampleSize.Should().Be(5);
        settings.MaxCancellationRatio.Should().Be(0.7m);
        settings.MaxAmendmentsPerOrder.Should().Be(3.0m);
        settings.MinFillRatio.Should().Be(0.1m);
        settings.ShortLivedCancelThreshold.Should().Be(TimeSpan.FromSeconds(2));
        settings.MaxShortLivedCancels.Should().Be(3);
        settings.LayeringOrderCount.Should().Be(3);
    }

    [Fact]
    public void 運用段階の既定値はStage0のペーパーモードである()
    {
        var stage = TradingDefaults.CreateStageSettings();

        stage.Stage.Should().Be(TradingStage.Stage0Verification);
        stage.Mode.Should().Be(TradeMode.Paper);
        stage.CapitalCap.Should().Be(100_000m); // 初期投入資金（利用者決定 2026-07-07）
    }

    // FR-20, ADR-0008: 段階ゲート方針の既定。Stage 0/1＝ペーパー、Stage 2/3＝実弾。撤退倍率 1.5。
    [Fact]
    public void 段階ゲート方針の既定は段階別モードと資金上限を定義する()
    {
        var policy = TradingDefaults.CreateStagePolicy();

        policy.WithdrawalDrawdownMultiple.Should().Be(1.5m); // ADR-0008: 実DD ≥ バックテスト最大DD × 1.5

        policy.SettingsFor(TradingStage.Stage0Verification)
            .Should().Be(new StageSettings(TradingStage.Stage0Verification, TradeMode.Paper, 100_000m));
        policy.SettingsFor(TradingStage.Stage1Paper)
            .Should().Be(new StageSettings(TradingStage.Stage1Paper, TradeMode.Paper, 100_000m));
        // Stage 2 最小実弾: 実弾モード・保守的暫定既定（1 ポジション相当）
        policy.SettingsFor(TradingStage.Stage2MinimalLive)
            .Should().Be(new StageSettings(TradingStage.Stage2MinimalLive, TradeMode.Live, 35_000m));
        // Stage 3 段階増額: 実弾モード・初期投入資金まで
        policy.SettingsFor(TradingStage.Stage3ScaledLive)
            .Should().Be(new StageSettings(TradingStage.Stage3ScaledLive, TradeMode.Live, 100_000m));
    }
}
