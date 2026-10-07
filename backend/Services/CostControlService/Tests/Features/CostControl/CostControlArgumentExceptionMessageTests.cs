using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using CostControlService.Features.CostControl;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CostControlService.Tests;

// NFR-06, IADR-0503, #1206: 費用統制の群のフィルタは、フレームワークが投げた ArgumentException の文言を応答へ載せず
// 固定文言にする（400 は維持）。
public class CostControlArgumentExceptionMessageTests
{
    // 接続文字列に似た目印（資格情報は含めない）。応答に出たら漏れである。
    private const string ConnectionLikeMarker = "Host=cost-db.internal;Port=5432;Database=costs;Username=cost_app";

    // どのメンバーを呼んでもフレームワーク（System.Text.RegularExpressions）の ArgumentException を投げる台帳。
    public class FrameworkThrowingLedger : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            _ = new Regex(ConnectionLikeMarker + "(");
            throw new InvalidOperationException("Regex が例外を投げなかった（前提の崩れ）。");
        }
    }

    // T-10-2402: フレームワークの ArgumentException は 400 を保ち、文言は固定文言（目印を返さない）。
    [Fact]
    public async Task フレームワークの_ArgumentException_は_400_で固定文言()
    {
        await using var baseFactory = new CostControlWorkerWebApplicationFactory();
        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddScoped(_ => DispatchProxy.Create<ICostLedger, FrameworkThrowingLedger>())));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");

        using var res = await client.GetAsync("/costs/state", TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonNode.Parse(body)!["error"]!.GetValue<string>().Should().Be(ClientFacingErrors.InvalidRequestMessage);
        body.Should().NotContain("cost-db.internal");
    }
}
