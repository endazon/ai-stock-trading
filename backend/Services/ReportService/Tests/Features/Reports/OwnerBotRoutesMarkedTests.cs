using System.Net;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ReportService.Tests;

// 🔴 NFR-06, FR-14, #1554, IADR-0622: 報告書の**本物の組み立て**で、ボットの許可表の印（AllowOwnerBot）が付いた経路が
// 許可表（OwnerBotRoutes）の報告書の分（/reports）と**集合で一致**すること（印の付け忘れ・付けすぎを止める）。
// 許可表とボットの REST アダプタの経路の一致は、通知サービスの試験（OwnerBotRouteCoverageTests）が確かめる。
// あわせて、ボットのクライアントのトークンが印の無い所有者の経路へ来たときの段（Auth:OwnerBot:Mode）の振る舞いを固定する。
public class OwnerBotRoutesMarkedTests
{
    private const string Prefix = "/reports";

    private static HashSet<(string Method, string Template)> MarkedRoutes(IServiceProvider services) =>
        services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<OwnerBotOperation>() is not null)
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(m => (m, OwnerBotRoutes.Normalize(e.RoutePattern.RawText ?? string.Empty))))
            .ToHashSet();

    [Fact]
    public void 印の付いた経路は許可表の報告書の分と一致する()
    {
        using var factory = new ReportWorkerWebApplicationFactory();
        _ = factory.CreateClient();

        var expected = OwnerBotRoutes.All
            .Where(r => r.NormalizedTemplate == Prefix || r.NormalizedTemplate.StartsWith(Prefix + "/", StringComparison.Ordinal))
            .Select(r => (r.Method, r.NormalizedTemplate))
            .ToHashSet();

        expected.Should().NotBeEmpty("許可表に報告書の経路がある（空どうしの一致は何も証明しない）");
        MarkedRoutes(factory.Services).Should().BeEquivalentTo(expected);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, string azp, string roles = "trading-owner")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        client.DefaultRequestHeaders.Add(TestAuthHandler.AzpHeader, azp);
        return client;
    }

    private static WebApplicationFactory<Program> WithBotMode(ReportWorkerWebApplicationFactory factory, string? mode) =>
        mode is null ? factory : factory.WithWebHostBuilder(b => b.UseSetting(OwnerBotScope.ModeKey, mode));

    [Theory]
    [InlineData(null, false)]
    [InlineData("Report", false)]
    [InlineData("Enforce", true)]
    public async Task ボットのトークンが許可表の外の所有者の経路へ来たら段2だけ403(string? mode, bool forbidden)
    {
        using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = (WebApplicationFactory<Program>)WithBotMode(baseFactory, mode);

        var outside = await Client(factory, "ai-stock-trading-owner").GetAsync("/reports/daily-2026-01-05");
        var inside = await Client(factory, "ai-stock-trading-owner").GetAsync("/reports/period-keys");
        var user = await Client(factory, "bff").GetAsync("/reports/daily-2026-01-05");

        (outside.StatusCode == HttpStatusCode.Forbidden).Should().Be(forbidden, "許可表の外は段 2 だけ拒む");
        inside.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, "許可表の経路は段に関係なく通る");
        user.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, "利用者のトークンは許可表を見ない");
    }

    [Fact]
    public async Task ボット専用のロールは許可表の経路でだけ通る()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();

        (await Client(factory, "ai-stock-trading-owner", "trading-bot").GetAsync("/reports/period-keys")).StatusCode
            .Should().NotBe(HttpStatusCode.Forbidden);
        (await Client(factory, "ai-stock-trading-owner", "trading-bot").GetAsync("/reports/daily-2026-01-05")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "段 1 でも許可表の外は通さない");
        (await Client(factory, "bff", "trading-bot").GetAsync("/reports/period-keys")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "ボット以外のクライアントは通さない");
    }
}
