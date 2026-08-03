using System.Net;
using System.Net.Http.Json;
using AiStockTrading.MarketMonitor.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.MarketMonitor.Worker.Tests;

// FR-03, FR-13, ADR-0007: 監視設定エンドポイントの認可（OwnerOnly）と永続化・反映を検証する。
public class MonitorSettingsEndpointsTests(MonitorWorkerWebApplicationFactory factory)
    : IClassFixture<MonitorWorkerWebApplicationFactory>
{
    private HttpClient OwnerClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    [Fact]
    public async Task 未認証の設定取得は401()
    {
        var res = await factory.CreateClient().GetAsync("/monitor/settings");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 利用者ロールを持たない場合は403()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "viewer");

        var res = await client.GetAsync("/monitor/settings");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 利用者は監視設定を更新でき永続化される()
    {
        var client = OwnerClient();
        var request = new
        {
            MovementThresholdRatio = 0.05m,
            Cooldown = TimeSpan.FromMinutes(10),
            MonitoredSymbols = new[] { new MonitoredSymbol("AAPL", Market.UnitedStates) },
        };

        var put = await client.PutAsJsonAsync("/monitor/settings", request);
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var settings = await client.GetFromJsonAsync<SettingsDto>("/monitor/settings");
        settings!.MovementThresholdRatio.Should().Be(0.05m);
        settings.MonitoredSymbols.Should().ContainSingle(s => s.Symbol == "AAPL");
    }

    [Fact]
    public async Task 不正な閾値は400()
    {
        var client = OwnerClient();
        var request = new
        {
            MovementThresholdRatio = 0m, // 不正（正でない）
            Cooldown = TimeSpan.FromMinutes(10),
            MonitoredSymbols = Array.Empty<MonitoredSymbol>(),
        };

        var res = await client.PutAsJsonAsync("/monitor/settings", request);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ヘルスチェック_live_は認証不要で応答する()
    {
        var res = await factory.CreateClient().GetAsync("/health/live");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record SettingsDto(decimal MovementThresholdRatio, List<MonitoredSymbol> MonitoredSymbols);
}
