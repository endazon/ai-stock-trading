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
        store.GetCurrent().Stage.CapitalCap.Should().Be(TradingDefaults.InitialCapital);
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
    public void 有効時はペーパー段階の資金上限が上がり解決後の金額上限も比例する()
    {
        using var factory = Factory(enabled: true);
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRiskSettingsStore>();

        store.Should().BeOfType<SimulatorProfileRiskSettingsStore>();
        var current = store.GetCurrent();
        // FR-10, #329, IADR-0130 決定6: 上限は equity 比のまま。解決額が基準資金に比例して上がる。
        current.Limits.MaxOrderAmountFor(SimulatorTradingDefaults.InitialCapital).Should().Be(42_500_000m);
        current.Limits.MaxDailyOrderAmountFor(SimulatorTradingDefaults.InitialCapital).Should().Be(255_000_000m);
        current.Stage.Mode.Should().Be(TradeMode.Paper);
        current.Stage.CapitalCap.Should().Be(SimulatorTradingDefaults.InitialCapital);
    }

    [Fact]
    public void 有効時も実弾段階の資金上限は本番既定のまま()
    {
        // 検証用フラグで実弾（Stage 2/3）のリスク上限が動かないことを配線レベルでも固定する。
        using var factory = Factory(enabled: true);
        _ = factory.CreateClient();

        var policy = factory.Services.GetRequiredService<StageGatePolicy>();

        policy.SettingsFor(TradingStage.Stage2MinimalLive).CapitalCap
            .Should().Be(TradingDefaults.Stage2MinimalLiveCapitalCap);
        policy.SettingsFor(TradingStage.Stage3ScaledLive).CapitalCap
            .Should().Be(TradingDefaults.InitialCapital);
        policy.SettingsFor(TradingStage.Stage1Simulate).CapitalCap
            .Should().Be(SimulatorTradingDefaults.InitialCapital);
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
