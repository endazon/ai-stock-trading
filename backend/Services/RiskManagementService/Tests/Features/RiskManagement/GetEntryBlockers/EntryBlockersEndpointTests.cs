using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.GetEntryBlockers;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace RiskManagementService.Tests;

// T-10-1785・T-10-1786・T-10-1787, FR-10, FR-04, #1113, IADR-0463 決定 3, IADR-0420 決定 2, IADR-0427:
// 新規建ての可否の口（REST `GET /risk-controls/entry-blockers`・gRPC `GetEntryBlockers`）を**本物の Program.cs**
// （RiskWorkerWebApplicationFactory。InMemory DB・TestAuthHandler だけを差し替え）で固定する。
public class EntryBlockersEndpointTests
{
    private const string Service = "trading-service";
    private const string Path = "/risk-controls/entry-blockers?symbol=AAPL&market=1";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static HttpClient Client(RiskWorkerWebApplicationFactory factory, string? roles)
    {
        var client = factory.CreateClient();
        if (roles is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    private static GrpcChannel Channel(RiskWorkerWebApplicationFactory factory) =>
        GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = new Roles(factory.Server.CreateHandler()),
        });

    private sealed class Roles(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation(TestAuthHandler.RolesHeader, Service);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static void EngageKillSwitch(RiskWorkerWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IKillSwitchStore>()
            .SetState(new KillSwitchState(true, "owner", "試験", DateTimeOffset.UtcNow));
    }

    // T-10-1785: OwnerOrService（判断が s2s で読む）。未認証 401・ロールなし 403・引数の欠落 400。
    [Fact]
    public async Task T_10_1785_認可と引数の検証()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();

        (await Client(factory, null).GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client(factory, "viewer").GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var service = Client(factory, Service);
        (await service.GetAsync("/risk-controls/entry-blockers?market=1")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await service.GetAsync("/risk-controls/entry-blockers?symbol=AAPL")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await service.GetAsync("/risk-controls/entry-blockers?symbol=AAPL&market=9")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await service.GetAsync("/risk-controls/entry-blockers?symbol=AAPL&market=UnitedStates")).StatusCode
            .Should().Be(HttpStatusCode.OK, "市場は名前でも受ける");
    }

    // T-10-1785 / T-10-1787: 🔴 本番の配線で、kill switch の状態が口に出る（Program.cs の登録・ストアの結線）。
    // 本文は応答型を web 既定（camelCase・列挙は数値）で直列化したものと一字一句同じ（受け手の契約テストの前提。IADR-0420 決定 2）。
    [Fact]
    public async Task T_10_1787_本番の配線で状態が口に出て本文は応答型の_web_既定の直列化と同じ()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        var service = Client(factory, Service);

        var clear = await service.GetFromJsonAsync<EntryBlockersView>(Path, Web);
        clear!.LongSide.Should().BeEmpty();
        clear.ShortSide.Should().BeEmpty();

        EngageKillSwitch(factory);
        var body = JsonNode.Parse(await service.GetStringAsync(Path));

        using var scope = factory.Services.CreateScope();
        var view = scope.ServiceProvider.GetRequiredService<EntryBlockersService>().Build("AAPL", Market.UnitedStates);
        view.LongSide.Should().Equal(RejectionReason.KillSwitchActive);
        JsonNode.DeepEquals(body, JsonSerializer.SerializeToNode(view, Web))
            .Should().BeTrue($"entry-blockers の本文が web 既定と異なる: {body?.ToJsonString()}");
        body!["longSide"]!.AsArray().Single()!.GetValue<int>().Should().Be((int)RejectionReason.KillSwitchActive);
        body["market"]!.GetValue<int>().Should().Be((int)Market.UnitedStates);
    }

    // T-10-1786: gRPC は REST と同じ値（同じサービス）。欠落は INVALID_ARGUMENT。
    [Fact]
    public async Task T_10_1786_gRPC_は_REST_と同じ値を返し欠落は_INVALID_ARGUMENT()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        EngageKillSwitch(factory);
        var rest = await Client(factory, Service).GetFromJsonAsync<EntryBlockersView>(Path, Web);
        using var channel = Channel(factory);
        var grpc = new Proto.RiskControlsRead.RiskControlsReadClient(channel);

        var response = await grpc.GetEntryBlockersAsync(
            new Proto.GetEntryBlockersRequest { Symbol = "AAPL", Market = Proto.Market.UnitedStates });

        rest!.LongSide.Should().NotBeEmpty("空どうしの一致は何も証明しない");
        response.Should().Be(RiskReadWireMapping.ToProto(rest));
        response.LongSide.Reasons.Should().Equal(Proto.EntryBlocker.KillSwitchActive);
        response.ShortSide.Reasons.Should().Equal(Proto.EntryBlocker.KillSwitchActive);

        foreach (var bad in new[]
                 {
                     new Proto.GetEntryBlockersRequest { Market = Proto.Market.UnitedStates },
                     new Proto.GetEntryBlockersRequest { Symbol = "AAPL" },
                     new Proto.GetEntryBlockersRequest { Symbol = " ", Market = Proto.Market.UnitedStates },
                 })
        {
            var act = async () => await grpc.GetEntryBlockersAsync(bad);
            (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        }
    }

    // T-10-1786: 口が返し得る理由はすべて線上の値を持つ（名前で写す）。対象外の理由は写さず例外（黙って落とさない）。
    [Fact]
    public void T_10_1786_口の理由はすべて名前で線上へ写り対象外は例外()
    {
        foreach (var reason in EntryStateBlockers.Determinable)
        {
            var wire = RiskReadWireMapping.ToProto(reason);
            wire.Should().NotBe(Proto.EntryBlocker.Unspecified);
            wire.ToString().Should().Be(reason.ToString(), "名前で写す");
        }

        var act = () => RiskReadWireMapping.ToProto(RejectionReason.StopOutStatusUnknown);
        act.Should().Throw<ArgumentOutOfRangeException>();

        var empty = RiskReadWireMapping.ToProto(new EntryBlockersView("AAPL", Market.Japan, [], [], []));
        empty.LongSide.Should().NotBeNull("空の入れ物は「確定する拒否は無い」（欠落＝不明と区別する）");
        empty.ShortSide.Should().NotBeNull();
        empty.AnyOrder.Should().NotBeNull("#1286, IADR-0521 決定 4: 全注文の拒否も空の入れ物で送る（欠落＝不明と区別する）");
        empty.Market.Should().Be(Proto.Market.Japan, "日本は線上で 1（未指定の 0 ではない）");
    }
}
