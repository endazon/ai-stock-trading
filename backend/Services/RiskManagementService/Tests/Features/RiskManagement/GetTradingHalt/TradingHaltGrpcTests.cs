using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.GetTradingHalt;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace RiskManagementService.Tests;

// 🔴 T-10-3082, NFR, FR-10, ADR-0009, MSP:ADR-0029, IADR-0284 決定 5（段 5 の追補 3）, IADR-0567 決定 2, IADR-0604, #1510 (#753):
// 停止の状態の**gRPC 面**（`RiskControlsRead/GetTradingHalt`）を**本物の Program.cs**（RiskWorkerWebApplicationFactory）で固定する。
// REST（GET /risk-controls/trading-halt）の本文を送り手の本物の型へ戻し、提供側の写し（RiskReadWireMapping）で proto にしたものと
// gRPC の応答を message ごと等価比較する。false も「在る値」として運ぶ（`Has*` が立つ）＝受け手が欠落と区別できる。
public class TradingHaltGrpcTests
{
    private const string Service = "trading-service";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static GrpcChannel ChannelFor(WebApplicationFactory<Program> factory, string? roles)
    {
        HttpMessageHandler handler = factory.Server.CreateHandler();
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task T_10_3082_停止の状態は_REST_と同じ値で_false_も在る値として運ぶ(bool killSwitch, bool paused)
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var now = DateTimeOffset.UtcNow;
            scope.ServiceProvider.GetRequiredService<IKillSwitchStore>().SetState(new KillSwitchState(killSwitch, "owner", "試験", now));
            scope.ServiceProvider.GetRequiredService<IPauseStore>().SetState(new PauseState(paused, "owner", "試験", now));
        }

        var rest = factory.CreateClient();
        rest.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, Service);
        var restView = await rest.GetFromJsonAsync<TradingHaltView>(
            "/risk-controls/trading-halt", Web, TestContext.Current.CancellationToken);

        using var channel = ChannelFor(factory, Service);
        var grpc = await new Proto.RiskControlsRead.RiskControlsReadClient(channel)
            .GetTradingHaltAsync(new Proto.GetTradingHaltRequest(), cancellationToken: TestContext.Current.CancellationToken);

        restView.Should().Be(new TradingHaltView(killSwitch, paused));
        grpc.Should().Be(RiskReadWireMapping.ToProto(restView!));
        (grpc.HasKillSwitchEngaged, grpc.KillSwitchEngaged).Should().Be((true, killSwitch));
        (grpc.HasTradingPaused, grpc.TradingPaused).Should().Be((true, paused));
    }

    // 陰性対照: 門は他の読み取りと同じ（資格情報なし＝UNAUTHENTICATED、ロール不足＝PERMISSION_DENIED）。
    [Theory]
    [InlineData(null, StatusCode.Unauthenticated)]
    [InlineData("some-unrelated-role", StatusCode.PermissionDenied)]
    public async Task T_10_3082_門は既定で閉じる(string? roles, StatusCode expected)
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        using var channel = ChannelFor(factory, roles);

        var act = async () => await new Proto.RiskControlsRead.RiskControlsReadClient(channel)
            .GetTradingHaltAsync(new Proto.GetTradingHaltRequest(), cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(expected);
    }
}
