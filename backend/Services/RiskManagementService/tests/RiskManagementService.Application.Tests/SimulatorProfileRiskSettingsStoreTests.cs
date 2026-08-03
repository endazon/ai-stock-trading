using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-10, FR-12, FR-20, #257, IADR-0108 決定3/4: SIMULATE 限定プロファイルの読み取り時適用。
// 「上限だけ差し替える」「実弾段階は書き換えない」「DB（内側ストア）は汚さない」を固定する。
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

    [Fact]
    public void 有効時は金額系の上限をプロファイル値へ差し替える()
    {
        var inner = new RecordingStore(Production());
        var store = new SimulatorProfileRiskSettingsStore(inner);

        var current = store.GetCurrent();

        current.Limits.MaxOrderAmount.Should().Be(59_500_000m);
        current.Limits.MaxDailyOrderAmount.Should().Be(170_000_000m);
        current.Stage.CapitalCap.Should().Be(SimulatorTradingDefaults.InitialCapital);
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

    [Fact]
    public void 実弾段階の資金上限は上書きしない()
    {
        // 検証用プロファイルが実弾（Stage 2/3）のリスク上限を緩められないことを固定する（IADR-0108 決定4）。
        var live = Production(TradingStage.Stage2MinimalLive);
        var store = new SimulatorProfileRiskSettingsStore(new RecordingStore(live));

        var current = store.GetCurrent();

        current.Stage.Mode.Should().Be(TradeMode.Live);
        current.Stage.CapitalCap.Should().Be(TradingDefaults.Stage2MinimalLiveCapitalCap);
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
        // 保存後も読み取りは上限だけ差し替わる（内側の編集内容＝保有数は保たれる）。
        store.GetCurrent().Limits.MaxOpenPositions.Should().Be(2);
        store.GetCurrent().Limits.MaxOrderAmount.Should().Be(59_500_000m);
    }

    [Fact]
    public void 未適用_内側ストアのみ_なら本番既定のまま()
    {
        // 無効時はデコレータを噛ませない配線のため、内側ストアの値がそのまま返る（現行等価）。
        IRiskSettingsStore inner = new RecordingStore(Production());

        inner.GetCurrent().Limits.MaxOrderAmount.Should().Be(35_000m);
        inner.GetCurrent().Stage.CapitalCap.Should().Be(TradingDefaults.InitialCapital);
    }
}
