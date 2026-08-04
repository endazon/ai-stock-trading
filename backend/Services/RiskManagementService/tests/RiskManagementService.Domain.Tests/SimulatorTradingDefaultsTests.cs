using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Domain.Tests;

// FR-10, FR-12, FR-17, FR-20, #257, #329, IADR-0108, IADR-0130 決定6: SIMULATE 限定リスク上限プロファイルの
// 値と不変条件。金額系を equity 比で保持するようになったため（計画 05_trading-assumptions §5）、
// プロファイルが差し替えるのは**基準資金とペーパー段階の資金上限だけ**である
// （上限額は基準資金に比例して自動的に上がる＝比率はスケール不変）。
public class SimulatorTradingDefaultsTests
{
    [Fact]
    public void 基準資金はシミュレータ残高に基づく()
    {
        // $1,000,000 × ¥150 ＋ ¥20,000,000 = ¥170,000,000。
        SimulatorTradingDefaults.InitialCapital.Should().Be(170_000_000m);
    }

    // FR-10, #329, IADR-0130 決定6: 金額系の上限は基準資金に比例して解決される（プロファイル固有の
    // スケール係数を持たない）。旧実装の 1,700 倍スケールが不要になったことをここで固定する。
    [Fact]
    public void 金額系の上限は基準資金に比例して解決される()
    {
        var limits = SimulatorTradingDefaults.CreateSettings().Limits;

        limits.MaxOrderAmountFor(SimulatorTradingDefaults.InitialCapital).Should().Be(42_500_000m);      // 170,000,000 × 25%
        limits.MaxDailyOrderAmountFor(SimulatorTradingDefaults.InitialCapital).Should().Be(255_000_000m); // × 150%
    }

    [Fact]
    public void 上限の比率は本番既定と同一である()
    {
        // 比率はスケール不変量。変えるとリスクモデル自体が変わるため据え置く（IADR-0108 決定1）。
        var production = TradingDefaults.CreateRiskLimits();
        var simulator = SimulatorTradingDefaults.CreateSettings().Limits;

        simulator.Should().Be(production);
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
        // FR-10, #329, ADR-0018: 初期資金は $3,000（基準通貨換算 ¥491,100）、金額系は equity 比。
        TradingDefaults.InitialEquityUsd.Should().Be(3_000m);
        TradingDefaults.InitialCapital.Should().Be(491_100m);
        TradingDefaults.CreateRiskLimits().MaxOrderAmountRatio.Should().Be(0.25m);
        TradingDefaults.CreateRiskLimits().MaxDailyOrderAmountRatio.Should().Be(1.50m);
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

        var limits = SimulatorTradingDefaults.CreateSettings().Limits;
        var simulatorQuantity = PositionSizer.CalculateCappedQuantity(
            SimulatorTradingDefaults.InitialCapital,
            limits.PerTradeRiskRatio,
            stopLossDistanceInBase,
            referencePriceInBase,
            limits.MaxOrderAmountFor(SimulatorTradingDefaults.InitialCapital),
            availableCapital: SimulatorTradingDefaults.InitialCapital);

        // FR-10: 「厳しい方が効く」。リスク予算基準 = 170,000,000 × 1% ÷ 1,507.5 = 1,127 株、
        // 金額基準 = 42,500,000 ÷ 50,250 = 845 株。損切り幅 3% は 4%（＝1% ÷ 25%）より狭いため金額側が効く。
        simulatorQuantity.Should().Be(845);

        // #329: 本番既定（equity ¥491,100）でも米国株が建てられる（増資 $3,000 の目的）。
        // リスク予算基準 3 株・金額基準 122,775 ÷ 50,250 = 2 株 → 厳しい方の 2 株。
        var production = TradingDefaults.CreateRiskLimits();
        PositionSizer.CalculateCappedQuantity(
            TradingDefaults.InitialCapital,
            production.PerTradeRiskRatio,
            stopLossDistanceInBase,
            referencePriceInBase,
            production.MaxOrderAmountFor(TradingDefaults.InitialCapital),
            availableCapital: TradingDefaults.InitialCapital)
            .Should().Be(2);
    }
}
