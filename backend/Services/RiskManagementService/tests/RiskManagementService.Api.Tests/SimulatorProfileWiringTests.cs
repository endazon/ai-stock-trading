using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.RiskManagement.Api.Tests;

// FR-10, FR-12, FR-20, #257, IADR-0108: SIMULATE 限定プロファイルの配線。
// 「既定は本番既定のまま」「有効時だけ上限と基準資金が上がる」「実弾段階は有効時も不変」を固定する。
public class SimulatorProfileWiringTests
{
    private static RiskWorkerWebApplicationFactory Factory(bool? enabled = null) =>
        enabled is null
            ? new RiskWorkerWebApplicationFactory()
            : new RiskWorkerWebApplicationFactory
            {
                HostSettings = new Dictionary<string, string?>
                {
                    ["Risk:SimulatorProfile:Enabled"] = enabled.Value ? "true" : "false",
                },
            };

    [Fact]
    public void 既定は本番既定の上限のまま()
    {
        using var factory = Factory();
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRiskSettingsStore>();

        store.Should().NotBeOfType<SimulatorProfileRiskSettingsStore>();
        store.GetCurrent().Limits.MaxOrderAmountRatio.Should().Be(0.25m);
        store.GetCurrent().Stage.CapitalCapRatio.Should().Be(TradingDefaults.FullCapitalCapRatio);
    }

    [Fact]
    public void 明示的に無効化しても本番既定のまま()
    {
        using var factory = Factory(enabled: false);
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IRiskSettingsStore>()
            .Should().NotBeOfType<SimulatorProfileRiskSettingsStore>();
    }

    [Fact]
    public void 有効時は基準資金だけが上がり解決後の金額上限が比例する()
    {
        using var factory = Factory(enabled: true);
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRiskSettingsStore>();

        store.Should().BeOfType<SimulatorProfileRiskSettingsStore>();
        var current = store.GetCurrent();
        // FR-10, #329, IADR-0130 決定6: 上限は equity 比のまま。解決額が基準資金に比例して上がる。
        current.Limits.MaxOrderAmountFor(SimulatorTradingDefaults.InitialCapital).Should().Be(283_333.25m);
        current.Limits.MaxDailyOrderAmountFor(SimulatorTradingDefaults.InitialCapital).Should().Be(1_699_999.50m);
        current.Stage.Mode.Should().Be(BrokerProvider.InternalPaper);
        // FR-20, #333, IADR-0136: 段階の発注可能額も総資金比のため、比率は本番既定のまま解決額だけが比例する。
        current.Stage.CapitalCapRatio.Should().Be(TradingDefaults.FullCapitalCapRatio);
        current.Stage.OrderableCapFor(SimulatorTradingDefaults.InitialCapital)
            .Should().Be(SimulatorTradingDefaults.InitialCapital);
    }

    // **否定形**: 検証用フラグで実弾（Stage 2/3）の発注可能額が動かないことを配線レベルでも固定する。
    // #333・IADR-0136 以降、段階ゲート方針は本番既定（TradingDefaults）だけが供給する。
    [Fact]
    public void 有効時も段階ゲート方針は本番既定のまま()
    {
        using var factory = Factory(enabled: true);
        _ = factory.CreateClient();

        var policy = factory.Services.GetRequiredService<StageGatePolicy>();

        // record の等値比較は Definitions（辞書）を参照比較するため、内容で突き合わせる。
        policy.Should().BeEquivalentTo(TradingDefaults.CreateStagePolicy());
        policy.SettingsFor(TradingStage.Stage2MinimalLive).CapitalCapRatio
            .Should().Be(TradingDefaults.Stage2MinimalLiveCapitalCapRatio);
        policy.SettingsFor(TradingStage.Stage3ScaledLive).CapitalCapRatio
            .Should().Be(TradingDefaults.FullCapitalCapRatio);
        policy.SettingsFor(TradingStage.Stage1Simulate).CapitalCapRatio
            .Should().Be(TradingDefaults.FullCapitalCapRatio);
    }

    [Fact]
    public void 有効時は基準資金もシミュレータ残高になる()
    {
        // サイジング文脈（SizingContext.Capital）の起点＝台帳射影の初期資金。空台帳では基準資金そのものが出る。
        using var enabled = Factory(enabled: true);
        _ = enabled.CreateClient();
        using var enabledScope = enabled.Services.CreateScope();

        enabledScope.ServiceProvider.GetRequiredService<IPortfolioStateProvider>().GetCurrent().Capital
            .Should().Be(SimulatorTradingDefaults.InitialCapital);

        using var disabled = Factory();
        _ = disabled.CreateClient();
        using var disabledScope = disabled.Services.CreateScope();

        disabledScope.ServiceProvider.GetRequiredService<IPortfolioStateProvider>().GetCurrent().Capital
            .Should().Be(TradingDefaults.InitialCapital);
    }
}
