using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Domain.Tests;

// FR-10, FR-12, FR-17, FR-20, #257, #329, IADR-0108, IADR-0130 決定6: SIMULATE 限定リスク上限プロファイルの
// 値と不変条件。金額系を equity 比で保持するようになったため（計画 05_trading-assumptions §5）、
// プロファイルが差し替えるのは**基準資金とペーパー段階の資金上限だけ**である
// （上限額は基準資金に比例して自動的に上がる＝比率はスケール不変）。
public class SimulatorTradingDefaultsTests
{
    [Fact]
    public void 基準資金はシミュレータ残高に基づく()
    {
        // #364, IADR-0152 決定4: 基準通貨は USD。$1,000,000 ＋ ¥20,000,000 ÷ ¥150/USD ≒ $1,133,333（切り捨て）。
        SimulatorTradingDefaults.SimulatorJpyBalanceInUsd.Should().Be(133_333m);
        SimulatorTradingDefaults.InitialCapital.Should().Be(1_133_333m);
    }

    // FR-10, #329, IADR-0130 決定6: 金額系の上限は基準資金に比例して解決される（プロファイル固有の
    // スケール係数を持たない）。旧実装の 1,700 倍スケールが不要になったことをここで固定する。
    [Fact]
    public void 金額系の上限は基準資金に比例して解決される()
    {
        var limits = SimulatorTradingDefaults.CreateSettings().Limits;

        limits.MaxOrderAmountFor(SimulatorTradingDefaults.InitialCapital).Should().Be(283_333.25m);       // 1,133,333 × 25%
        limits.MaxDailyOrderAmountFor(SimulatorTradingDefaults.InitialCapital).Should().Be(1_699_999.50m); // × 150%
    }

    [Fact]
    public void 上限の比率は本番既定と同一である()
    {
        // 比率はスケール不変量。変えるとリスクモデル自体が変わるため据え置く（IADR-0108 決定1）。
        var production = TradingDefaults.CreateRiskLimits();
        var simulator = SimulatorTradingDefaults.CreateSettings().Limits;

        simulator.Should().Be(production);
    }

    // **否定形**（FR-20, #333, IADR-0136）: 検証用プロファイルが段階の発注可能額を差し替える経路が
    // **もう存在しない**ことを固定する。比率はスケール不変であり、差し替えは不要になった
    // （旧 SimulatorTradingDefaults.ApplyToPaperStage / CreateStagePolicy は削除済み）。
    // 「検証用フラグで実弾段階の上限を動かさない」（IADR-0108 決定4）は構造的に成立する。
    [Fact]
    public void 段階の発注可能額を差し替える経路を持たない()
    {
        typeof(SimulatorTradingDefaults).GetMethod("ApplyToPaperStage")
            .Should().BeNull("段階の発注可能額は総資金比であり、プロファイルが差し替える対象は無い");
        typeof(SimulatorTradingDefaults).GetMethod("CreateStagePolicy")
            .Should().BeNull("段階ゲート方針は本番既定（TradingDefaults）だけが供給する");

        // 段階設定そのものも本番既定と同一である（基準資金だけがプロファイル値になる）。
        SimulatorTradingDefaults.CreateStageSettings().Should().Be(TradingDefaults.CreateStageSettings());
    }

    [Fact]
    public void 本番既定は変更しない()
    {
        // プロファイルの存在で本番既定が動いていないことを明示的に固定する。
        // FR-10, #329, #364, ADR-0018: 初期資金は $3,000（基準通貨 USD そのもの）、金額系は equity 比。
        TradingDefaults.InitialEquityUsd.Should().Be(3_000m);
        TradingDefaults.InitialCapital.Should().Be(3_000m);
        TradingDefaults.CreateRiskLimits().MaxOrderAmountRatio.Should().Be(0.25m);
        TradingDefaults.CreateRiskLimits().MaxDailyOrderAmountRatio.Should().Be(1.50m);
        TradingDefaults.CreateStagePolicy().SettingsFor(TradingStage.Stage2MinimalLive).CapitalCapRatio
            .Should().Be(TradingDefaults.Stage2MinimalLiveCapitalCapRatio);
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
        // #257, #364: AAPL $335/株。基準通貨が USD になったため、米国株は換算せずそのまま基準通貨額である。
        const decimal referencePriceInBase = 335m;
        var stopLossDistanceInBase = referencePriceInBase * TradingDefaults.DefaultStopLossRatio; // 3%

        var limits = SimulatorTradingDefaults.CreateSettings().Limits;
        var simulatorQuantity = PositionSizer.CalculateCappedQuantity(
            SimulatorTradingDefaults.InitialCapital,
            limits.PerTradeRiskRatio,
            stopLossDistanceInBase,
            referencePriceInBase,
            limits.MaxOrderAmountFor(SimulatorTradingDefaults.InitialCapital),
            availableCapital: SimulatorTradingDefaults.InitialCapital);

        // FR-10: 「厳しい方が効く」。リスク予算基準 = 1,133,333 × 1% ÷ 10.05 = 1,127 株、
        // 金額基準 = 283,333.25 ÷ 335 = 845 株。損切り幅 3% は 4%（＝1% ÷ 25%）より狭いため金額側が効く。
        // **比率はスケール不変**であるため、基準通貨を JPY から USD へ移しても株数は変わらない（IADR-0152 決定8）。
        simulatorQuantity.Should().Be(845);

        // #329: 本番既定（equity $3,000）でも米国株が建てられる（増資 $3,000 の目的）。
        // リスク予算基準 2 株・金額基準 750 ÷ 335 = 2 株 → 厳しい方の 2 株（移行前と同じ株数）。
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
