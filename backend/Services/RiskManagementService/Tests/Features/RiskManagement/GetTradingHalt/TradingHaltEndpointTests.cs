using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.GetTradingHalt;
using Xunit;

namespace RiskManagementService.Tests;

// 🔴 T-10-2970, FR-10, ADR-0009, IADR-0565 決定 2, IADR-0420 決定 2: 停止の状態の口（GET /risk-controls/trading-halt）を
// **本物の Program.cs**（RiskWorkerWebApplicationFactory。InMemory DB・TestAuthHandler だけを差し替え）で固定する。
// 発注執行が s2s（trading-service）で読む。本文は応答型を web 既定で直列化したものと同じで、操作者・理由は出さない。
public class TradingHaltEndpointTests
{
    private const string Path = "/risk-controls/trading-halt";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static HttpClient Client(RiskWorkerWebApplicationFactory factory, string? roles)
    {
        var client = factory.CreateClient();
        if (roles is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    [Fact]
    public async Task 認可はOwnerOrService()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();

        (await Client(factory, null).GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client(factory, "viewer").GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client(factory, "trading-service").GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Client(factory, "trading-owner").GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task サービスの資格情報では停止の操作も操作者つきの現況も読めないまま()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        var service = Client(factory, "trading-service");

        (await service.GetAsync("/risk-controls/kill-switch")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await service.GetAsync("/risk-controls/pause")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task 審査と同じストアの状態が本文に出て本文は応答型の_web_既定の直列化と同じ(bool killSwitch, bool paused)
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var now = DateTimeOffset.UtcNow;
            scope.ServiceProvider.GetRequiredService<IKillSwitchStore>()
                .SetState(new KillSwitchState(killSwitch, "owner", "試験", now));
            scope.ServiceProvider.GetRequiredService<IPauseStore>()
                .SetState(new PauseState(paused, "owner", "試験", now));
        }

        var body = await Client(factory, "trading-service").GetFromJsonAsync<JsonNode>(Path);

        using var read = factory.Services.CreateScope();
        var view = read.ServiceProvider.GetRequiredService<TradingHaltService>().Build();
        view.Should().Be(new TradingHaltView(killSwitch, paused));
        JsonNode.DeepEquals(body, JsonSerializer.SerializeToNode(view, Web))
            .Should().BeTrue($"本文が web 既定と異なる: {body?.ToJsonString()}");

        // 受け手が依存している項目名を名指しで表明し、操作者・理由は出さない。
        body!.AsObject().Select(p => p.Key).Should().BeEquivalentTo(["killSwitchEngaged", "tradingPaused"]);
        body["killSwitchEngaged"]!.GetValue<bool>().Should().Be(killSwitch);
        body["tradingPaused"]!.GetValue<bool>().Should().Be(paused);
    }
}
