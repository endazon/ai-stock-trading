using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Domain.Tests;

// FR-10, FR-12, FR-17, FR-20, #257, IADR-0108: SIMULATE 限定リスク上限プロファイルの値と不変条件。
// 「シミュレータ残高に基づく金額」「比率は本番と同一」「実弾段階は不変」を固定する。
public class SimulatorTradingDefaultsTests
{
    [Fact]
    public void 金額系の上限はシミュレータ残高に基づく()
    {
        // $1,000,000 × ¥150 ＋ ¥20,000,000 = ¥170,000,000（本番既定 ¥100,000 の 1,700 倍）。
        SimulatorTradingDefaults.InitialCapital.Should().Be(170_000_000m);
        SimulatorTradingDefaults.ScaleFactor.Should().Be(1_700m);

        var limits = SimulatorTradingDefaults.CreateRiskLimits();

        limits.MaxOrderAmount.Should().Be(59_500_000m);      // ¥35,000 × 1,700（資金比 35% を維持）
        limits.MaxDailyOrderAmount.Should().Be(170_000_000m); // 本番と同じ「基準資金と同額」
    }

    [Fact]
    public void 比率系は本番既定と同一である()
    {
        // 比率はスケール不変量。変えるとリスクモデル自体が変わるため据え置く（IADR-0108 決定1）。
        var production = TradingDefaults.CreateRiskLimits();
        var simulator = SimulatorTradingDefaults.CreateRiskLimits();

        simulator.PerTradeRiskRatio.Should().Be(production.PerTradeRiskRatio);
        simulator.DailyLossLimitRatio.Should().Be(production.DailyLossLimitRatio);
        simulator.MaxDrawdownRatio.Should().Be(production.MaxDrawdownRatio);
        simulator.LosingStreakThreshold.Should().Be(production.LosingStreakThreshold);
        simulator.LosingStreakSizeFactor.Should().Be(production.LosingStreakSizeFactor);
        simulator.MaxOpenPositions.Should().Be(production.MaxOpenPositions);
    }

    [Fact]
    public void 実弾段階の資金上限は本番既定から変えない()
    {
        // 検証用プロファイルが実弾のリスク上限を緩められる経路を作らない（IADR-0108 決定4）。
        var production = TradingDefaults.CreateStagePolicy();
        var simulator = SimulatorTradingDefaults.CreateStagePolicy();

        foreach (var stage in new[] { TradingStage.Stage2MinimalLive, TradingStage.Stage3ScaledLive })
        {
            simulator.SettingsFor(stage).Should().Be(production.SettingsFor(stage));
            simulator.SettingsFor(stage).Mode.Should().Be(TradeMode.Live);
        }

        simulator.WithdrawalDrawdownMultiple.Should().Be(production.WithdrawalDrawdownMultiple);
    }

    [Fact]
    public void ペーパー段階の資金上限だけを引き上げる()
    {
        var simulator = SimulatorTradingDefaults.CreateStagePolicy();

        foreach (var stage in new[] { TradingStage.Stage0Verification, TradingStage.Stage1Paper })
        {
            simulator.SettingsFor(stage).Mode.Should().Be(TradeMode.Paper);
            simulator.SettingsFor(stage).CapitalCap.Should().Be(SimulatorTradingDefaults.InitialCapital);
        }
    }

    [Fact]
    public void 本番既定は変更しない()
    {
        // プロファイルの存在で本番既定が動いていないことを明示的に固定する。
        TradingDefaults.InitialCapital.Should().Be(100_000m);
        TradingDefaults.CreateRiskLimits().MaxOrderAmount.Should().Be(35_000m);
        TradingDefaults.CreateRiskLimits().MaxDailyOrderAmount.Should().Be(100_000m);
        TradingDefaults.CreateStagePolicy().SettingsFor(TradingStage.Stage2MinimalLive).CapitalCap
            .Should().Be(TradingDefaults.Stage2MinimalLiveCapitalCap);
    }

    [Fact]
    public void 取引ガードは本番既定と同一である()
    {
        // 禁止銘柄・有効市場・商品種別（FR-19）は検証環境でも緩めない。
        var simulator = SimulatorTradingDefaults.CreateSettings();

        // record の等値比較は集合を参照比較するため、内容で突き合わせる。
        simulator.Guard.Should().BeEquivalentTo(TradingDefaults.CreateGuardSettings());
    }

    [Fact]
    public void 米国株の代表銘柄でも数量が算出される()
    {
        // #257: AAPL $335 × ¥150 = ¥50,250/株（IADR-0107 で基準通貨へ換算済みの 1 株あたり金額）。
        const decimal referencePriceInBase = 50_250m;
        var stopLossDistanceInBase = referencePriceInBase * TradingDefaults.DefaultStopLossRatio; // 3%

        var simulatorLimits = SimulatorTradingDefaults.CreateRiskLimits();
        var simulatorQuantity = PositionSizer.CalculateCappedQuantity(
            SimulatorTradingDefaults.InitialCapital,
            simulatorLimits.PerTradeRiskRatio,
            stopLossDistanceInBase,
            referencePriceInBase,
            simulatorLimits.MaxOrderAmount,
            availableCapital: SimulatorTradingDefaults.InitialCapital);

        // リスク予算基準 = 170,000,000 × 1% ÷ 1,507.5 = 1,127 株（金額基準 1,184 株より小さい方）。
        simulatorQuantity.Should().Be(1_127);

        // 本番既定では 1 株（¥50,250）が 1 注文金額上限（¥35,000）を超えるため 0 株＝見送りになる。
        var productionLimits = TradingDefaults.CreateRiskLimits();
        PositionSizer.CalculateCappedQuantity(
            TradingDefaults.InitialCapital,
            productionLimits.PerTradeRiskRatio,
            stopLossDistanceInBase,
            referencePriceInBase,
            productionLimits.MaxOrderAmount,
            availableCapital: TradingDefaults.InitialCapital)
            .Should().Be(0);
    }
}
