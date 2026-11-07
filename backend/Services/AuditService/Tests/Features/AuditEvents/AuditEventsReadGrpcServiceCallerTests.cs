using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.Audit.V1;

namespace AuditService.Tests;

// 🔴 NFR, IADR-0637: gRPC の口の名前（サービスの型の [ServiceCallers(ServiceCallerNames.GrpcAuditEventsRead)]）が、段 Enforce で
// ServiceCallerHandler に届くこと。届かない（エンドポイントのメタデータから名前が取れない）と、一覧に在る呼び手（報告書）まで 403
// （PERMISSION_DENIED）になる。REST の口は ServiceCallerPolicyTests・ServiceCallerAllowlistTests が見る。gRPC は本試験が 1 つの型で見る。
// 本物の Program.cs（AuditWorkerWebApplicationFactory。TestAuthHandler の azp は X-Test-Azp）に TestServer 越しの h2c で通す。
public class AuditEventsReadGrpcServiceCallerTests
{
    private static WebApplicationFactory<Program> Factory(string? mode)
    {
        var factory = new AuditWorkerWebApplicationFactory();
        return mode is null ? factory : factory.WithWebHostBuilder(b => b.UseSetting(ServiceTokenBinding.ModeKey, mode));
    }

    private static Proto.AuditEventsRead.AuditEventsReadClient Client(WebApplicationFactory<Program> factory, string azp)
    {
        var handler = new Headers(factory.Server.CreateHandler(), azp);
        return new(GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = handler }));
    }

    private sealed class Headers(HttpMessageHandler inner, string azp) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation(TestAuthHandler.RolesHeader, "trading-service");
            request.Headers.TryAddWithoutValidation(TestAuthHandler.AzpHeader, azp);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static Proto.GetEventsByTypeRequest Request()
    {
        var request = new Proto.GetEventsByTypeRequest { From = "2026-09-10T00:00:00Z", To = "2026-09-11T00:00:00Z" };
        request.EventTypes.Add("OrderApproved");
        return request;
    }

    [Fact]
    public async Task Enforce_gRPCの口は一覧に在る報告書のクライアントだけを通し_ほかはPermissionDenied()
    {
        await using var factory = Factory("Enforce");
        (await Client(factory, "ai-stock-trading-svc-report").GetEventsByTypeAsync(Request())).Records.Should().BeEmpty();

        foreach (var other in new[] { "ai-stock-trading-svc-trade-decision", "ai-stock-trading-svc-market-monitor", "ai-stock-trading-svc" })
        {
            var act = async () => await Client(factory, other).GetEventsByTypeAsync(Request());
            (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied, $"{other} は監査の gRPC の口の一覧に無い");
        }
    }

    [Fact]
    public async Task 陰性対照_段Offでは一覧に無いサービスのクライアントも従前どおり通る()
    {
        await using var factory = Factory(null);
        (await Client(factory, "ai-stock-trading-svc-trade-decision").GetEventsByTypeAsync(Request())).Records.Should().BeEmpty();
    }
}
