using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderExecutionService.Features.OrderExecution.QueryDailyBars;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-1837, FR-04, FR-15, #1118, IADR-0420 決定2, IADR-0467 決定 2: **本番の Program.cs が `GET /order-execution/daily-bars`
// に出す JSON は、応答型 `DailyBarsView` を web 既定（camelCase・列挙は数値・日付は yyyy-MM-dd）で直列化したものと一字一句同じである**
// ことを固定する（受け手＝取引判断の HttpDailyBarsSource・T-10-1836 の前提）。あわせて口の守り（匿名は 401・値域外は 400 で OpenD を撃たない）と、
// 内蔵 paper の本物の組み立てでは OpenD の口が無く Unavailable を返すことを固定する。
public class DailyBarsReadContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string OkPath = "/order-execution/daily-bars?symbol=AAPL&market=1&from=2026-08-14&to=2026-09-28";

    private static readonly DailyBarView Bar1 = new(new DateOnly(2026, 9, 25), 250.5m, 252m, 249.25m, 251.75m, 41_234_567);
    private static readonly DailyBarView Bar2 = new(new DateOnly(2026, 9, 28), 251.75m, 255.5m, 250m, 254.125m, 52_345_678);

    private static WebApplicationFactory<Program> Wire(ExecutionWorkerWebApplicationFactory factory, IDailyKLineSource? source) =>
        factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            // 内蔵 paper 構成は日足のポートを登録しない。答え（OpenD の境界）だけを足し、口・サービスの組み立て
            // （Program.cs の構築式が GetService で拾う）・直列化は本物のまま組む。
            services.RemoveAll<IDailyKLineSource>();
            if (source is not null)
                services.AddSingleton(source);
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        }));

    [Fact]
    public async Task 日足の本文は応答型を_web_既定で直列化したものと同じ()
    {
        await using var factory = new ExecutionWorkerWebApplicationFactory();
        using var wired = Wire(factory, new DailyBarsQueryServiceTests.FakeSource(_ => new DailyKLineFetch(true, [Bar1, Bar2], null, 3, 297)));
        var client = wired.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-service");

        var res = await client.GetAsync(OkPath, TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

        var expected = new DailyBarsView(
            "AAPL", Market.UnitedStates, DailyBarsStatus.Available, null, new DateOnly(2026, 8, 14), new DateOnly(2026, 9, 28), [Bar1, Bar2]);
        JsonNode.DeepEquals(body, JsonSerializer.SerializeToNode(expected, Web))
            .Should().BeTrue($"日足の本文が web 既定と異なる: {body.ToJsonString()}");
        body["status"]!.GetValue<int>().Should().Be((int)DailyBarsStatus.Available);
        body["bars"]![1]!["date"]!.GetValue<string>().Should().Be("2026-09-28");
        body["bars"]![1]!["volume"]!.GetValue<long>().Should().Be(52_345_678);
    }

    [Theory]
    [InlineData(OkPath, null, HttpStatusCode.Unauthorized)]
    [InlineData("/order-execution/daily-bars?market=1&from=2026-08-14&to=2026-09-28", "trading-service", HttpStatusCode.BadRequest)]
    [InlineData("/order-execution/daily-bars?symbol=AAPL&from=2026-08-14&to=2026-09-28", "trading-service", HttpStatusCode.BadRequest)]
    [InlineData("/order-execution/daily-bars?symbol=AAPL&market=7&from=2026-08-14&to=2026-09-28", "trading-service", HttpStatusCode.BadRequest)]
    [InlineData("/order-execution/daily-bars?symbol=AAPL&market=1&to=2026-09-28", "trading-service", HttpStatusCode.BadRequest)]
    [InlineData("/order-execution/daily-bars?symbol=AAPL&market=1&from=2026-09-29&to=2026-09-28", "trading-service", HttpStatusCode.BadRequest)]
    [InlineData("/order-execution/daily-bars?symbol=AAPL&market=1&from=2025-08-24&to=2026-09-28", "trading-service", HttpStatusCode.BadRequest)]
    [InlineData("/order-execution/daily-bars?symbol=AAPL&market=1&from=2025-08-25&to=2026-09-28", "trading-service", HttpStatusCode.OK)]
    [InlineData("/order-execution/daily-bars?symbol=ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456&market=1&from=2026-08-14&to=2026-09-28", "trading-service", HttpStatusCode.BadRequest)]
    [InlineData(OkPath, "trading-owner", HttpStatusCode.OK)]
    public async Task 匿名は401で値域外は400になりOpenDを撃たない(string path, string? roles, HttpStatusCode expected)
    {
        await using var factory = new ExecutionWorkerWebApplicationFactory();
        var source = new DailyBarsQueryServiceTests.FakeSource(_ => new DailyKLineFetch(true, [Bar1], null, null, null));
        using var wired = Wire(factory, source);
        var client = wired.CreateClient();
        if (roles is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);

        var res = await client.GetAsync(path, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(expected);
        source.Requests.Should().HaveCount(expected == HttpStatusCode.OK ? 1 : 0);
    }

    // 内蔵 paper の本物の組み立て（日足のポートを登録しない）では、OpenD へ繋がず Unavailable（broker-not-supported）を返す。
    [Fact]
    public async Task 内蔵paperの組み立てではUnavailableを返す()
    {
        await using var factory = new ExecutionWorkerWebApplicationFactory();
        using var wired = Wire(factory, source: null);
        var client = wired.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-service");

        var res = await client.GetAsync(OkPath, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        body["status"]!.GetValue<int>().Should().Be((int)DailyBarsStatus.Unavailable);
        body["unavailableReason"]!.GetValue<string>().Should().Be(DailyBarsUnavailableReasons.BrokerNotSupported);
    }
}
