using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiStockTrading.Shared.Kernel.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using ConfigurationService.Features.Assumptions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ConfigurationService.Tests;

// NFR-06, FR-17, IADR-0503, #1206: 前提条件の群のフィルタは、自前の入力検証の 400 の文言を保ち、
// フレームワークが投げた ArgumentException の文言は固定文言にする（400 は維持）。
public class AssumptionsArgumentExceptionMessageTests
{
    // 接続文字列に似た目印（資格情報は含めない）。応答に出たら漏れである。
    private const string ConnectionLikeMarker = "Host=config-db.internal;Port=5432;Database=configuration;Username=config_app";

    // どのメンバーを呼んでもフレームワーク（System.Text.RegularExpressions）の ArgumentException を投げる保存。
    public class FrameworkThrowingStore : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            _ = new Regex(ConnectionLikeMarker + "(");
            throw new InvalidOperationException("Regex が例外を投げなかった（前提の崩れ）。");
        }
    }

    private static HttpClient Owner(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    // T-10-2400: フレームワークの ArgumentException は 400 を保ち、文言は固定文言（目印を返さない）。
    [Fact]
    public async Task フレームワークの_ArgumentException_は_400_で固定文言()
    {
        await using var baseFactory = new ConfigurationWorkerWebApplicationFactory();
        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddScoped(_ => DispatchProxy.Create<IAssumptionsStore, FrameworkThrowingStore>())));

        using var res = await Owner(factory).GetAsync("/assumptions", TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonNode.Parse(body)!["error"]!.GetValue<string>().Should().Be(ClientFacingErrors.InvalidRequestMessage);
        body.Should().NotContain("config-db.internal");
    }

    // T-10-2401: 自前の入力検証（理由が空）は 400 で文言を保つ（NFR-06, IADR-0509, #1230: 理由の空欄検査は印つきの ClientVisibleArgument.ThrowIfNullOrWhiteSpace）。
    [Fact]
    public async Task 自前の入力検証の_ArgumentException_は_400_で文言を保つ()
    {
        await using var factory = new ConfigurationWorkerWebApplicationFactory();

        using var res = await Owner(factory).PutAsJsonAsync(
            "/assumptions",
            new { Assumptions = TradingAssumptionsDefaults.Create(), ExpectedVersion = 1, Reason = "" },
            TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonNode.Parse(body)!["error"]!.GetValue<string>().Should().Contain("'reason'");
    }
}
