using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Events;
using FluentAssertions;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.CostControl.Worker.Tests;

// NFR（費用）, IADR-0027: 費用計上・統制判定・費用レビューエンドポイントの OwnerOnly・しきい値イベント発行を検証する。
// 月次台帳は可変状態のためテストごとに独立 Factory（独立 InMemory DB）。
public class CostControlEndpointsTests
{
    private const string OwnerRole = "trading-owner";

    private static HttpClient OwnerClient(CostControlWorkerWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);
        return client;
    }

    private static object Record(string category, decimal amount) => new { Category = category, Amount = amount };

    [Fact]
    public async Task 未認証は_401_ロール無しは_403()
    {
        await using var factory = new CostControlWorkerWebApplicationFactory();

        (await factory.CreateClient().GetAsync("/costs/state")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var noRole = factory.CreateClient();
        noRole.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "other");
        (await noRole.GetAsync("/costs/state")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task LLM費用が80パーセント到達で_CostThresholdReached_発行_状態も_Throttled()
    {
        await using var factory = new CostControlWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        // LLM 上限 15,000 の 80% = 12,000 を計上。
        (await client.PostAsJsonAsync("/costs/record", Record("Llm", 12_000m))).StatusCode.Should().Be(HttpStatusCode.OK);

        var harness = factory.Services.GetRequiredService<ITestHarness>();
        (await harness.Published.Any<CostThresholdReached>()).Should().BeTrue();

        var state = await client.GetFromJsonAsync<StateDto>("/costs/state");
        state!.State.Should().Be("Throttled");
        state.IntervalMultiplier.Should().Be(2m);
    }

    [Fact]
    public async Task 費用レビューは費用対資金比率を返す()
    {
        await using var factory = new CostControlWorkerWebApplicationFactory();
        var client = OwnerClient(factory);
        await client.PostAsJsonAsync("/costs/record", Record("Llm", 2_000m));

        var review = await client.GetFromJsonAsync<ReviewDto>("/costs/review?capital=100000");

        review!.Ratio.Should().Be(0.02m);
    }

    private sealed record StateDto(string State, decimal IntervalMultiplier);

    private sealed record ReviewDto(decimal Ratio);
}
