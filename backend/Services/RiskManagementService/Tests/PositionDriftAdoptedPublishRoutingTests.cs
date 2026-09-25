using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;
using Xunit;

namespace RiskManagementService.Tests;

// 🔴 T-10-1011（送り手側）, FR-10, UC-06, #879, #858, IADR-0424 決定3, IADR-0129 決定2・3:
// **本番の Program.cs のリスク管理が、乖離の取り込み（PositionDriftAdopted）の発行をプロセス内へ閉じず、型名の共有 exchange へ向ける**。
//
// 受け手（発注執行）は同じ名前の exchange を宣言し、自分の購読キューを束縛する（発注執行側の T-10-1011 が本番の Program.cs で固定する）。
// 発行がローカルへ閉じる（DisableConventionalLocalRouting の欠落）か exchange の名前がずれると、送り手・受け手の試験はどちらも緑のまま
// 取り込みが発注執行へ 1 通も届かず、取り込みで消えた建玉の保護逆指値が残る（IADR-0370）。
public class PositionDriftAdoptedPublishRoutingTests
{
    [Fact]
    public async Task Programは取り込みイベントを型名の共有exchangeへ発行しプロセス内へ閉じない()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        var runtime = factory.Services.GetRequiredService<IWolverineRuntime>();

        var routes = runtime.RoutingFor(typeof(PositionDriftAdopted)).Routes.Select(r => r.ToString()).ToList();

        routes.Should().NotBeEmpty("取り込みは発注執行・監査・通知が購読する（発行先が無ければ誰にも届かない）");
        routes.Should().AllSatisfy(r => r.Should().NotContain(
            "local://", "発行がプロセス内へ閉じると他サービスへ届かない（IADR-0129 決定3）"));
        routes.Should().Contain(
            r => r.Contains("rabbitmq://exchange/" + typeof(PositionDriftAdopted).FullName, StringComparison.Ordinal),
            "受け手（発注執行）が購読キューを束縛する exchange と同じ名前（型名）で発行する（IADR-0129 決定2）");
    }
}
