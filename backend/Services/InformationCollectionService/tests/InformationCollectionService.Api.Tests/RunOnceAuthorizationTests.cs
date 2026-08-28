using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Xunit;

namespace InformationCollectionService.Api.Tests;

// NFR（セキュリティ）, FR-02, UC-01, #456, IADR-0176:
// **run-once（収集サイクルの起動）は業務操作であり、無認証で公開しない。**
//
// **この統制は「緩む方向」に壊れても静かである** —— 認可を外しても収集は動き続け、何も赤くならない。
// したがって**両側で固定する**。本クラスは**振る舞い**（401/403/通過）を、
// `UnauthenticatedEndpointsNotAllowedTests` は**構造**（経路の不在）を受け持つ。
public class RunOnceAuthorizationTests(InformationCollectionWorkerWebApplicationFactory factory)
    : IClassFixture<InformationCollectionWorkerWebApplicationFactory>
{
    private const string RunOnce = "/internal/collection/run-once";

    // 否定形: トークンが無ければ収集サイクルを起こせない。
    [Fact]
    public async Task 認証なしでrun_onceを起動できない()
    {
        var response = await factory.CreateClient().PostAsync(RunOnce, null);

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "run-once は業務操作であり無認証で公開しない（#456・IADR-0176 決定1）");
    }

    // 否定形: 認証は通るがロールを持たないトークンでも起こせない（認証と認可を取り違えない）。
    [Theory]
    [InlineData("some-other-role")]
    [InlineData("trading-reader")]
    public async Task 権限のないロールではrun_onceを起動できない(string role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, role);

        (await client.PostAsync(RunOnce, null)).StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            $"'{role}' は trading-owner でも trading-service でもない");
    }

    // 肯定形（対照）: 検査器が空振りしていないこと。**許可されたロールでは実際に通る**。
    // これが無いと「常に 401 を返すだけの壊れた配線」でも否定形テストが緑になる。
    [Theory]
    [InlineData(AiStockTradingAuthPolicies.OwnerRole)]
    [InlineData(AiStockTradingAuthPolicies.ServiceRole)]
    public async Task 利用者またはサービスのロールならrun_onceを起動できる(string role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, role);

        var response = await client.PostAsync(RunOnce, null);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "CronJob は trading-service で叩く。ここが通らないと本番スケジューラが動かない");
    }

    // ヘルスチェックは無認証のままであること（liveness/readiness に認可を掛けると kubelet が落とせなくなる）。
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task ヘルスチェックは認証を要求しない(string path)
    {
        (await factory.CreateClient().GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 配線の実測: run-once に認可メタデータが載っていること（ルート表から直接読む）。
    [Fact]
    public void run_onceのルートに認可メタデータが載っている()
    {
        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SingleOrDefault(e => e.RoutePattern.RawText?.Contains("run-once", StringComparison.Ordinal) == true);

        endpoint.Should().NotBeNull("run-once の経路そのものが消えていたら本検査は空振りしている");
        endpoint!.Metadata.GetMetadata<IAuthorizeData>().Should().NotBeNull();
    }
}
