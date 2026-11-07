using System.Net;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ReportService.Tests;

// 🔴 NFR, NFR-06, IADR-0637, IADR-0634: 監視銘柄の入れ替えの適用の結び付きの口（照会・予約）は、段 Enforce ではサービスの中でも市場監視の
// クライアントだけが呼べる（ほかのサービス・旧の共有クライアントは 403）。サービスの身元だけの門（ServiceOnly）はそのまま残る。
// 本物の Program.cs（ReportWorkerWebApplicationFactory。TestAuthHandler の azp は X-Test-Azp）。案が無いので通った呼び手は 403 以外（404 など）になる。
public class WatchlistProposalBindingServiceCallerTests
{
    private const string Binding = "/reports/policy-revisions/watchlist-proposal/binding?periodKey=2026-11-06&version=1";
    private const string Reserve = "/reports/policy-revisions/watchlist-proposal/binding/reserve?periodKey=2026-11-06&version=1";

    private static WebApplicationFactory<Program> Factory(string? mode)
    {
        var factory = new ReportWorkerWebApplicationFactory();
        return mode is null ? factory : factory.WithWebHostBuilder(b => b.UseSetting(ServiceTokenBinding.ModeKey, mode));
    }

    private static HttpClient Service(WebApplicationFactory<Program> factory, string azp)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-service");
        client.DefaultRequestHeaders.Add(TestAuthHandler.AzpHeader, azp);
        return client;
    }

    private static async Task<HttpStatusCode> CallAsync(HttpClient client, bool reserve) =>
        (reserve ? await client.PostAsync(Reserve, content: null) : await client.GetAsync(Binding)).StatusCode;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Enforce_結び付きの口は市場監視のクライアントだけ(bool reserve)
    {
        await using var factory = Factory("Enforce");
        (await CallAsync(Service(factory, "ai-stock-trading-svc-market-monitor"), reserve))
            .Should().NotBe(HttpStatusCode.Forbidden, "市場監視は一覧に在る（案が無いので 404 などで返る）");
        foreach (var other in new[] { "ai-stock-trading-svc-trade-decision", "ai-stock-trading-svc-report", "ai-stock-trading-svc-information-collection", "ai-stock-trading-svc" })
        {
            (await CallAsync(Service(factory, other), reserve)).Should().Be(HttpStatusCode.Forbidden, $"{other} は結び付きの口の一覧に無い");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 陰性対照_段Offでは一覧に無いサービスのクライアントも従前どおりサービスの門だけで通る(bool reserve)
    {
        await using var factory = Factory(null);
        (await CallAsync(Service(factory, "ai-stock-trading-svc-trade-decision"), reserve)).Should().NotBe(HttpStatusCode.Forbidden);
    }
}
