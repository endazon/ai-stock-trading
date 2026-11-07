using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.Infrastructure.Composable.SecondFactor;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace RiskManagementService.Tests;

// 🔴 NFR, NFR-06, IADR-0636: リスク管理の口は、既定の一覧（ServiceCallerNames.DefaultCallers。構成 Auth:ServiceCallers で置き換えられる）に在るサービスのクライアントだけが呼べる
// （段 Auth:ServiceToken:Mode=Enforce。既定 Off は従前どおり）。第 2 要素の確かめの口は報告書のサービスのクライアントだけ。
// 認証は TestAuthHandler（azp は X-Test-Azp）。宛先（aud）の 401 は認証の層の試験（PlatformShim の ServiceCallerPolicyTests）が持つ。
public class ServiceCallerAllowlistTests
{
    private const string Verify = "/risk-controls/owner-second-factor/verify";
    private static readonly OwnerSecondFactorVerifyRequest Body = new("ReportConfirm", "report:2026-11-06@v1", "000000");

    private static WebApplicationFactory<Program> Factory(string? mode, bool legacy = false)
    {
        var settings = new Dictionary<string, string?>
        {
            [$"{OwnerSecondFactorOptions.SectionName}:Mode"] = "Audit",
        };
        if (mode is not null)
            settings[ServiceTokenBinding.ModeKey] = mode;
        if (legacy)
            settings[$"{ServiceTokenBinding.LegacyClientsKey}:0"] = "ai-stock-trading-svc";
        return new RiskWorkerWebApplicationFactory { HostSettings = settings };
    }

    private static HttpClient Service(WebApplicationFactory<Program> factory, string azp)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-service");
        client.DefaultRequestHeaders.Add(TestAuthHandler.AzpHeader, azp);
        return client;
    }

    [Fact]
    public async Task Enforce_確かめの口は報告書のクライアントだけ()
    {
        using var factory = Factory("Enforce");
        (await Service(factory, "ai-stock-trading-svc-report").PostAsJsonAsync(Verify, Body)).StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var other in new[] { "trade-decision", "order-execution", "market-monitor", "information-collection", "backtest", "cost-control" })
        {
            (await Service(factory, $"ai-stock-trading-svc-{other}").PostAsJsonAsync(Verify, Body))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{other} は確かめの口を呼ばない");
        }

        (await Service(factory, "ai-stock-trading-svc").PostAsJsonAsync(Verify, Body)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "移行の段の外では旧の共有クライアントも通さない");
    }

    [Theory]
    [InlineData("/risk-controls/open-positions", "ai-stock-trading-svc-market-monitor", HttpStatusCode.OK)]
    [InlineData("/risk-controls/open-positions", "ai-stock-trading-svc-order-execution", HttpStatusCode.Forbidden)]
    [InlineData("/risk-controls/trading-halt", "ai-stock-trading-svc-order-execution", HttpStatusCode.OK)]
    [InlineData("/risk-controls/trading-halt", "ai-stock-trading-svc-trade-decision", HttpStatusCode.Forbidden)]
    [InlineData("/risk-controls/sizing-context", "ai-stock-trading-svc-trade-decision", HttpStatusCode.OK)]
    [InlineData("/risk-controls/sizing-context", "ai-stock-trading-svc-report", HttpStatusCode.Forbidden)]
    [InlineData("/risk-controls/stage-gate", "ai-stock-trading-svc-report", HttpStatusCode.OK)]
    [InlineData("/risk-controls/stage-gate", "ai-stock-trading-svc-trade-decision", HttpStatusCode.Forbidden)]
    public async Task Enforce_読み取りの口は一覧のクライアントだけ(string route, string azp, HttpStatusCode expected)
    {
        using var factory = Factory("Enforce");
        (await Service(factory, azp).GetAsync(route)).StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task 移行の段は旧の共有クライアントも通し_既定_Off_は従前どおり()
    {
        using (var migrating = Factory("Enforce", legacy: true))
        {
            (await Service(migrating, "ai-stock-trading-svc").PostAsJsonAsync(Verify, Body)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await Service(migrating, "ai-stock-trading-svc-trade-decision").PostAsJsonAsync(Verify, Body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        using var off = Factory(null);
        (await Service(off, "ai-stock-trading-svc-trade-decision").PostAsJsonAsync(Verify, Body)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Service(off, "ai-stock-trading-svc-order-execution").GetAsync("/risk-controls/open-positions")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
