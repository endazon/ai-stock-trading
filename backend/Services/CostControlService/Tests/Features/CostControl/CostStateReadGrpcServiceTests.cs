using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using CostControlService.Domain;
using CostControlService.Features.CostControl;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.CostControl.V1;

namespace CostControlService.Tests;

// T-10-1692（提供側）, NFR（費用）, FR-01, MSP:ADR-0029, IADR-0284 決定 5（段 4）, IADR-0446 決定 2・3, #1061 (#753):
// 費用統制の判定の **gRPC 面**。REST（`GET /costs/state`）と**同じ値・同じ認可**であることを、本物の Program.cs
// （CostControlWorkerWebApplicationFactory）で固定する。Normal・Throttled・Halted の 3 状態を本物の計上で作り、REST の本文を送り手の型へ
// 戻して提供側の写しで proto にしたものと gRPC の応答を message ごと等価比較する（在る false・在る 0 も値として運ぶ）。
public class CostStateReadGrpcServiceTests
{
    private const string ServiceRole = "trading-service";
    private static readonly JsonSerializerOptions CostWire =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static GrpcChannel ChannelFor(WebApplicationFactory<Program> factory, string? roles)
    {
        var handler = factory.Server.CreateHandler();
        if (roles is not null)
            handler = new RolesHeaderHandler(handler, roles);
        return GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = handler });
    }

    private sealed class RolesHeaderHandler(HttpMessageHandler inner, string roles) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation(TestAuthHandler.RolesHeader, roles);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static HttpClient Rest(CostControlWorkerWebApplicationFactory factory, string roles = ServiceRole)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    // LLM 上限 15,000（テストの構成）に対する計上額。0 = Normal、12,000 = 80%（Throttled）、15,000 = 100%（Halted）。
    [Theory]
    [InlineData(0, "Normal")]
    [InlineData(12_000, "Throttled")]
    [InlineData(15_000, "Halted")]
    public async Task T_10_1692_費用統制の判定は_REST_と同じ値を返す(int llmCost, string expectedState)
    {
        await using var factory = new CostControlWorkerWebApplicationFactory();
        if (llmCost > 0)
        {
            using var owner = Rest(factory, "trading-owner");
            (await owner.PostAsJsonAsync("/costs/record", new { Category = "Llm", Amount = (decimal)llmCost }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var rest = Rest(factory);
        using var channel = ChannelFor(factory, ServiceRole);

        var restDecision = await rest.GetFromJsonAsync<CostControlDecision>("/costs/state", CostWire);
        var grpc = await new Proto.CostStateRead.CostStateReadClient(channel).GetCostStateAsync(new Proto.GetCostStateRequest());

        restDecision!.State.ToString().Should().Be(expectedState);
        grpc.Should().Be(CostStateWireMapping.ToProto(restDecision));
        grpc.HasIsHalted.Should().BeTrue("在る false も値として運ぶ（欠落とは違う）");
        grpc.HasIntervalMultiplier.Should().BeTrue("Halted の倍率 0 も値として運ぶ");
    }

    [Fact]
    public async Task T_10_1692_資格情報が無ければ_UNAUTHENTICATED_ロール不足は_PERMISSION_DENIED()
    {
        await using var factory = new CostControlWorkerWebApplicationFactory();
        using var anonymous = ChannelFor(factory, roles: null);
        using var unrelated = ChannelFor(factory, "other");

        var anon = async () => await new Proto.CostStateRead.CostStateReadClient(anonymous).GetCostStateAsync(new Proto.GetCostStateRequest());
        var denied = async () => await new Proto.CostStateRead.CostStateReadClient(unrelated).GetCostStateAsync(new Proto.GetCostStateRequest());

        (await anon.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
        (await denied.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        (await factory.CreateClient().GetAsync("/costs/state")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "REST も同じ");
        using var restDenied = Rest(factory, "other");
        (await restDenied.GetAsync("/costs/state")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "REST も同じ");
    }

    [Fact]
    public async Task T_10_1692_利用者のロールでも読める()
    {
        await using var factory = new CostControlWorkerWebApplicationFactory();
        using var channel = ChannelFor(factory, "trading-owner");

        (await new Proto.CostStateRead.CostStateReadClient(channel).GetCostStateAsync(new Proto.GetCostStateRequest()))
            .IsHalted.Should().BeFalse();
    }
}
