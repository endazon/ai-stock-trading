using System.Net;
using System.Net.Http.Json;
using InformationCollectionService.Domain;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Xunit;

namespace InformationCollectionService.Tests;

// FR-01, FR-11, #336, ADR-0020 決定4: 一般インターネット収集（最終手段）の発動申請エンドポイント。
//
// 🔴 **認可は OwnerOnly**（run-once の OwnerOrService とは非対称）。ADR-0020 決定4 は**利用者の承認**を
// 要件としており、無人サービスが自動で発動してよいものではない。**この非対称は意図であり、揃えてはならない。**
public class GeneralWebActivationEndpointTests(InformationCollectionWorkerWebApplicationFactory factory)
    : IClassFixture<InformationCollectionWorkerWebApplicationFactory>
{
    private const string Path = "/internal/collection/general-web-activation";

    private static GeneralWebActivationRequest AllSatisfied() => new(
        Category: "news",
        OutageBusinessDays: 5,
        ProviderAnnouncedDiscontinuation: false,
        HarmConfirmedInReports: true,
        TermsPermitAutomatedAccess: true,
        DataSeparationApplied: true,
        CorroboratedByIndependentSources: true);

    [Fact]
    public async Task 認証なしでは発動申請できない()
    {
        var response = await factory.CreateClient().PostAsJsonAsync(Path, AllSatisfied());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // 🔴 否定形: **サービストークンでは発動できない**（利用者の承認が要件であるため）。
    [Fact]
    public async Task サービスロールでは発動申請できない()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, AiStockTradingAuthPolicies.ServiceRole);

        var response = await client.PostAsJsonAsync(Path, AllSatisfied());

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden, "一般 Web 収集の発動は利用者の承認を要する（ADR-0020 決定4）");
    }

    [Fact]
    public async Task 条件を満たさない申請は満たしていない条件つきで拒否される()
    {
        var client = OwnerClient();
        var request = AllSatisfied() with { OutageBusinessDays = 4, HarmConfirmedInReports = false };

        var response = await client.PostAsJsonAsync(Path, request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("条件1").And.Contain("条件2");
    }

    [Fact]
    public async Task 条件をすべて満たす申請は次回月報までの暫定期限つきで承認される()
    {
        var client = OwnerClient();

        var response = await client.PostAsJsonAsync(Path, AllSatisfied());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var decision = await response.Content.ReadFromJsonAsync<GeneralWebActivationDecision>();
        decision!.Approved.Should().BeTrue();
        decision.ProvisionalUntil.Should().NotBeNull("恒久化しない（次回月報までの暫定措置である）");
    }

    private HttpClient OwnerClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, AiStockTradingAuthPolicies.OwnerRole);
        return client;
    }
}
