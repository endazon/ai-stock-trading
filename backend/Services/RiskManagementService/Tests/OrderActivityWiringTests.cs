using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Domain;
using Microsoft.Extensions.DependencyInjection;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-19, #154, IADR-0067: Risk Worker の DI 配線で、注文履歴テレメトリの供給源と相場操縦検出器が解決でき、
// OrderScreeningService に検出器が注入される（本番経路で相場操縦判定が有効になる）ことを確かめる。
public class OrderActivityWiringTests : IClassFixture<RiskWorkerWebApplicationFactory>
{
    private readonly RiskWorkerWebApplicationFactory _factory;

    public OrderActivityWiringTests(RiskWorkerWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public void 注文アクティビティの供給源と相場操縦検出器が解決できる()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetService<IOrderActivityStore>().Should().NotBeNull();
        sp.GetService<IOrderActivitySource>().Should().NotBeNull();
        sp.GetService<IManipulativeOrderPatternDetector>().Should().NotBeNull(
            "登録により OrderScreeningService の GetService が非 null になり相場操縦判定が有効になる（IADR-0067）");
        // 発注審査本体も解決できる（検出器が注入される経路）。
        sp.GetService<OrderScreeningService>().Should().NotBeNull();
    }
}
