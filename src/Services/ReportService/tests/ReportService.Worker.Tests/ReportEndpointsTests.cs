using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Events;
using FluentAssertions;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.Report.Worker.Tests;

// FR-06/07, UC-03〜05, ADR-0007: 報告書エンドポイントの OwnerOnly 認可・ドラフト upsert・版番号付き冪等確定・
// 確定済み日報方針照会・確定イベント発行を WebApplicationFactory で検証する。テストごとに独立 Factory（独立 InMemory DB）。
public class ReportEndpointsTests
{
    private const string OwnerRole = "trading-owner";

    private static HttpClient OwnerClient(ReportWorkerWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);
        return client;
    }

    private static object DraftBody(string policy = "翌営業日は押し目買い", int expectedVersion = 0) => new
    {
        Kind = "Daily",
        PeriodStart = "2026-07-10",
        BasedOn = (string?)null,
        AssumptionsVersion = 1,
        PolicySummary = policy,
        ExpectedVersion = expectedVersion,
    };

    [Fact]
    public async Task 未認証は_401_ロール無しは_403()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();

        (await factory.CreateClient().GetAsync("/reports")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var noRole = factory.CreateClient();
        noRole.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "other");
        (await noRole.GetAsync("/reports")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ドラフト作成_確定で_ReportConfirmed_発行_daily_policy_照会()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        var put = await client.PutAsJsonAsync("/reports/daily-2026-07-10", DraftBody());
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var confirm = await client.PostAsJsonAsync("/reports/daily-2026-07-10/confirm", new { ExpectedVersion = 1 });
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);

        // ReportConfirmed が発行される。
        var harness = factory.Services.GetRequiredService<ITestHarness>();
        (await harness.Published.Any<ReportConfirmed>()).Should().BeTrue();

        // 確定済み日報方針が照会できる。
        var policy = await client.GetFromJsonAsync<DailyPolicyDto>("/reports/daily-policy");
        policy!.Summary.Should().Be("翌営業日は押し目買い");
        policy.AssumptionsVersion.Should().Be(1);
    }

    [Fact]
    public async Task 版番号が不一致の確定は_409()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        var client = OwnerClient(factory);
        await client.PutAsJsonAsync("/reports/daily-2026-07-10", DraftBody());

        var confirm = await client.PostAsJsonAsync("/reports/daily-2026-07-10/confirm", new { ExpectedVersion = 99 });

        confirm.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task 存在しない報告書の確定は_404()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();

        var confirm = await OwnerClient(factory).PostAsJsonAsync("/reports/daily-nope/confirm", new { ExpectedVersion = 1 });

        confirm.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task 確定が無ければ_daily_policy_は_404()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();

        (await OwnerClient(factory).GetAsync("/reports/daily-policy")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task 損益集計エンドポイントは実現損益を返す()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();

        var body = new
        {
            Fills = new[]
            {
                new { Symbol = "AAPL", Market = "UnitedStates", Side = "Buy", PositionEffect = "Open", Quantity = 10, Price = 1000m, ExecutedAt = "2026-07-10T00:00:00Z" },
                new { Symbol = "AAPL", Market = "UnitedStates", Side = "Sell", PositionEffect = "Close", Quantity = 10, Price = 1200m, ExecutedAt = "2026-07-10T00:01:00Z" },
            },
            CurrentPrices = (object?)null,
        };

        var res = await OwnerClient(factory).PostAsJsonAsync("/reports/pnl-summary", body);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var summary = await res.Content.ReadFromJsonAsync<PnlSummaryDto>();
        summary!.RealizedPnlGross.Should().Be(2_000m);
        summary.RealizingTradeCount.Should().Be(1);
    }

    private sealed record DailyPolicyDto(DateOnly Date, string Summary, int AssumptionsVersion);

    private sealed record PnlSummaryDto(decimal RealizedPnlGross, decimal RealizedPnlNet, int RealizingTradeCount);
}
