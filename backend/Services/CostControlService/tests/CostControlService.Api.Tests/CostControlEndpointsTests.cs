using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Tracking;
using Xunit;

namespace AiStockTrading.CostControl.Api.Tests;

// NFR（費用）, IADR-0027: 費用計上・統制判定・費用レビューエンドポイントの OwnerOnly・しきい値イベント発行を検証する。
// 月次台帳は可変状態のためテストごとに独立 Factory（独立 InMemory DB）。
public class CostControlEndpointsTests
{
    private const string OwnerRole = "trading-owner";
    private const string ServiceRole = "trading-service";

    private static HttpClient OwnerClient(CostControlWorkerWebApplicationFactory factory) =>
        ClientWithRoles(factory, OwnerRole);

    private static HttpClient ClientWithRoles(CostControlWorkerWebApplicationFactory factory, string roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
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

    // #79, IADR-0031/0051: 定時サイクル poller（サービス）は統制状態を照会できる必要がある一方、
    // 費用の書き込みは与えない（最小権限）。読み取り=OwnerOrService／書き込み=OwnerOnly の分離を固定する。
    [Fact]
    public async Task サービストークンは読み取りのみ_state_review_は200_record_は403()
    {
        await using var factory = new CostControlWorkerWebApplicationFactory();
        var service = ClientWithRoles(factory, ServiceRole);

        // 読み取り（poller が使う統制状態・月報の費用レビュー）は 200。
        (await service.GetAsync("/costs/state")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await service.GetAsync("/costs/review?capital=1000000")).StatusCode.Should().Be(HttpStatusCode.OK);

        // 書き込み（費用計上）は 403。サービスからの計上はイベント（LlmCostIncurred・IADR-0055）で行う。
        (await service.PostAsJsonAsync("/costs/record", Record("Llm", 100m)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // 利用者（owner）は読み書きとも可能（従来どおり）。
    [Fact]
    public async Task 利用者は読み取りも書き込みも可能()
    {
        await using var factory = new CostControlWorkerWebApplicationFactory();
        var owner = OwnerClient(factory);

        (await owner.GetAsync("/costs/state")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.PostAsJsonAsync("/costs/record", Record("Llm", 100m))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task LLM費用が80パーセント到達で_CostThresholdReached_発行_状態も_Throttled()
    {
        await using var factory = new CostControlWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        // LLM 上限 15,000 の 80% = 12,000 を計上。
        // ADR-0013, IADR-0129, #354: MassTransit の ITestHarness に代えて Wolverine.Tracking で発行を捕捉する。
        HttpResponseMessage recorded = null!;
        var session = await factory.Services.ExecuteAndWaitAsync(async () =>
        {
            recorded = await client.PostAsJsonAsync("/costs/record", Record("Llm", 12_000m));
        });
        recorded.StatusCode.Should().Be(HttpStatusCode.OK);

        session.Sent.MessagesOf<CostThresholdReached>().Should().NotBeEmpty();

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
