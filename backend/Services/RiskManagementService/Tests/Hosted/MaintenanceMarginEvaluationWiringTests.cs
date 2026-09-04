using RiskManagementService.Hosted;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, UC-06, ADR-0016 決定7, #634, IADR-0133, IADR-0298: 駆動の存在そのものを構造的に固定する。
//
// #634 が指摘した穴は「MaintenanceMarginReductionService は DI に登録されているが、それを解決して呼ぶ
// 本番コードが 1 行も無い」ことだった。**DI 登録だけを確認するテストは同じ穴を再発させる**——本テストは
// 実際の Program.cs の配線（RiskWorkerWebApplicationFactory 経由の実 DI）でホストを起動し、
// IHostedService の解決集合に MaintenanceMarginEvaluationService が実在することまで固定する。
public class MaintenanceMarginEvaluationWiringTests(RiskWorkerWebApplicationFactory factory)
    : IClassFixture<RiskWorkerWebApplicationFactory>
{
    private static bool HasDriver(IServiceProvider services) =>
        services.GetServices<IHostedService>().Any(s => s is MaintenanceMarginEvaluationService);

    // T-10-320: 既定（構成未設定）で駆動が実行経路に居る。既定有効という #634 の設計判断そのものを固定する。
    [Fact]
    public void 既定で維持率評価の駆動が登録される()
    {
        HasDriver(factory.Services).Should().BeTrue(
            "供給元が「供給なし」を返す間は構造的に不活性のため、駆動自体は既定で有効にする（#634・IADR-0298）");
    }

    // T-10-321: 明示的に無効化した場合は登録されない（緊急停止用の逃げ道が機能すること）。
    [Fact]
    public void 明示的に無効化すると駆動が登録されない()
    {
        using var disabled = factory.WithWebHostBuilder(
            b => b.UseSetting("MaintenanceMarginEvaluation:Enabled", "false"));

        HasDriver(disabled.Services).Should().BeFalse();
    }
}
