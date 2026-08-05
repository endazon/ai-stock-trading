using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-10, FR-12, FR-20, #257, #329, IADR-0108 決定3/4, IADR-0130 決定6: SIMULATE 限定プロファイルの読み取り時適用。
// 金額系が equity 比になったため、プロファイルが差し替えるのは**ペーパー段階の資金上限だけ**である。
// 「実弾段階は書き換えない」「DB（内側ストア）は汚さない」を固定する。
public class SimulatorProfileRiskSettingsStoreTests
{
    private sealed class RecordingStore(RiskManagementSettings current) : IRiskSettingsStore
    {
        public RiskManagementSettings Current { get; private set; } = current;

        public int Saves { get; private set; }

        public RiskManagementSettings GetCurrent() => Current;

        public void Save(RiskManagementSettings settings)
        {
            Saves++;
            Current = settings;
        }
    }

    private static RiskManagementSettings Production(TradingStage stage = TradingStage.Stage0Verification) =>
        new(TradingDefaults.CreateGuardSettings(),
            TradingDefaults.CreateRiskLimits(),
            TradingDefaults.CreateStagePolicy().SettingsFor(stage));

    // FR-20, #333, IADR-0136: 段階の発注可能額も**総資金比**になったため、プロファイルは段階設定も書き換えない。
    // 基準資金がシミュレータ残高になることで、解決後の発注可能額が比例して上がる。
    [Fact]
    public void 有効時も段階の発注可能額の比率は内側の設定のまま_解決額だけが基準資金に比例する()
    {
        var inner = new RecordingStore(Production());
        var store = new SimulatorProfileRiskSettingsStore(inner);

        var current = store.GetCurrent();

        current.Stage.Should().Be(TradingDefaults.CreateStagePolicy().SettingsFor(TradingStage.Stage0Verification));
        current.Stage.OrderableCapFor(SimulatorTradingDefaults.InitialCapital)
            .Should().Be(SimulatorTradingDefaults.InitialCapital);
    }

    // FR-10, #329, IADR-0130 決定6: 上限は equity 比のため、プロファイルは上限そのものを書き換えない。
    // 基準資金がシミュレータ残高になることで、解決後の上限額が比例して上がる。
    [Fact]
    public void 有効時も上限の比率は内側の設定のまま_解決額だけが基準資金に比例する()
    {
        var inner = new RecordingStore(Production());
        var store = new SimulatorProfileRiskSettingsStore(inner);

        var current = store.GetCurrent();

        current.Limits.Should().Be(TradingDefaults.CreateRiskLimits());
        current.Limits.MaxOrderAmountFor(SimulatorTradingDefaults.InitialCapital).Should().Be(42_500_000m);
        current.Limits.MaxDailyOrderAmountFor(SimulatorTradingDefaults.InitialCapital).Should().Be(255_000_000m);
    }

    [Fact]
    public void 有効時も比率系と取引ガードは内側の設定のまま()
    {
        var inner = new RecordingStore(Production());
        var store = new SimulatorProfileRiskSettingsStore(inner);

        var current = store.GetCurrent();

        current.Limits.PerTradeRiskRatio.Should().Be(TradingDefaults.CreateRiskLimits().PerTradeRiskRatio);
        current.Limits.MaxOpenPositions.Should().Be(TradingDefaults.CreateRiskLimits().MaxOpenPositions);
        current.Guard.Should().BeSameAs(inner.Current.Guard);
    }

    // **否定形**: 検証用プロファイルが実弾（Stage 2/3）の発注可能額を緩められないことを固定する
    // （IADR-0108 決定4）。#333・IADR-0136 以降、差し替える項目そのものが無いため構造的に成立する。
    [Fact]
    public void 実弾段階の発注可能額は上書きしない()
    {
        var live = Production(TradingStage.Stage2MinimalLive);
        var store = new SimulatorProfileRiskSettingsStore(new RecordingStore(live));

        var current = store.GetCurrent();

        current.Stage.Mode.Should().Be(BrokerProvider.MoomooReal);
        current.Stage.CapitalCapRatio.Should().Be(TradingDefaults.Stage2MinimalLiveCapitalCapRatio);
    }

    [Fact]
    public void 保存は素通しする()
    {
        // 永続化の権威は内側のストア。プロファイルはメモリ上の上書きに留め DB を汚さない。
        var inner = new RecordingStore(Production());
        var store = new SimulatorProfileRiskSettingsStore(inner);
        var edited = Production() with
        {
            Limits = TradingDefaults.CreateRiskLimits() with { MaxOpenPositions = 2 },
        };

        store.Save(edited);

        inner.Saves.Should().Be(1);
        inner.Current.Limits.MaxOpenPositions.Should().Be(2);
        // 保存後の読み取りも内側の値をそのまま返す（内側の編集内容＝保有数は保たれる）。
        store.GetCurrent().Limits.MaxOpenPositions.Should().Be(2);
        store.GetCurrent().Limits.MaxOrderAmountRatio.Should().Be(TradingDefaults.CreateRiskLimits().MaxOrderAmountRatio);
    }

    [Fact]
    public void 未適用_内側ストアのみ_なら本番既定のまま()
    {
        // 無効時はデコレータを噛ませない配線のため、内側ストアの値がそのまま返る（現行等価）。
        IRiskSettingsStore inner = new RecordingStore(Production());

        inner.GetCurrent().Limits.MaxOrderAmountRatio.Should().Be(0.25m);
        inner.GetCurrent().Stage.CapitalCapRatio.Should().Be(TradingDefaults.FullCapitalCapRatio);
    }
}
