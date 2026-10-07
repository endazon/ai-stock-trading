using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using MarketMonitorService.Domain;
using MarketMonitorService.Features.MarketMonitor;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketMonitorService.Tests;

// NFR-06, FR-13, IADR-0503, #1206: 市場監視の群のフィルタ（REST。gRPC の入れ替え案の適用も同じ写しを使う）は、
// 自前の入力検証の 400 の文言を保ち、フレームワークが投げた ArgumentException の文言は固定文言にする（400 は維持）。
public class WatchlistArgumentExceptionMessageTests
{
    // 接続文字列に似た目印（資格情報は含めない）。応答に出たら漏れである。
    private const string ConnectionLikeMarker = "Host=monitor-db.internal;Port=5432;Database=monitor;Username=monitor_app";

    // フレームワーク（System.Text.RegularExpressions）が実際に投げる ArgumentException。文言はパターン（目印）を引用する。
    private sealed class FrameworkThrowingStore : IMonitoredSymbolStore
    {
        public MarketMonitorSettings GetSettings()
        {
            _ = new Regex(ConnectionLikeMarker + "(");
            throw new InvalidOperationException("Regex が例外を投げなかった（前提の崩れ）。");
        }

        public void Save(MarketMonitorSettings settings) => GetSettings();
    }

    private static HttpClient Owner(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    // T-10-2398: フレームワークの ArgumentException は 400 を保ち、文言は固定文言（目印を返さない）。
    [Fact]
    public async Task フレームワークの_ArgumentException_は_400_で固定文言()
    {
        await using var baseFactory = new MonitorWorkerWebApplicationFactory();
        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            foreach (var d in s.Where(d => d.ServiceType == typeof(IMonitoredSymbolStore)).ToList()) s.Remove(d);
            s.AddScoped<IMonitoredSymbolStore, FrameworkThrowingStore>();
        }));

        using var res = await Owner(factory).GetAsync("/monitor/watchlist", TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonNode.Parse(body)!["error"]!.GetValue<string>().Should().Be(ClientFacingErrors.InvalidRequestMessage);
        body.Should().NotContain("monitor-db.internal");
    }

    // T-10-2399: 自前の入力検証（market の省略）は 400 で文言を保つ。
    [Fact]
    public async Task 自前の入力検証の_ArgumentException_は_400_で文言を保つ()
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();

        using var res = await Owner(factory).PostAsJsonAsync(
            "/monitor/watchlist", new { symbol = "AAPL", reason = "出来高" }, TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonNode.Parse(body)!["error"]!.GetValue<string>().Should().Contain("market は必須です。");
    }
}
