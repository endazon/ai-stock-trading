using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrderExecutionService.Features.OrderExecution.QueryDailyBars;
using OrderExecutionService.Infrastructure.ExternalServices;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-1846, FR-04, FR-15, #1118, IADR-0467 決定 1（［2026-10-01 追記］監査 🟢-1）: **本番の Program.cs の組み立て**で、
// 日足の読み取りポート（IDailyKLineSource）は moomoo 構成でだけ登録される。内蔵 paper では登録されず、口は OpenD へ繋がずに
// Unavailable（broker-not-supported）を返す。T-10-1837 の paper の試験はポートを自分で外す（RemoveAll）ため、
// Program.cs の `if (brokerSelection.IsMoomoo)` を外しても緑のままだった。ここでは差し替えずに組む。
public class DailyKLineSourceCompositionTests
{
    private const string OkPath = "/order-execution/daily-bars?symbol=AAPL&market=1&from=2026-08-14&to=2026-09-28";

    [Fact]
    public async Task 内蔵paperの本番の組み立てでは日足のポートを登録せずUnavailableを返す()
    {
        await using var baseFactory = new ExecutionWorkerWebApplicationFactory();
        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureServices(services =>
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { })));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-service");

        factory.Services.GetService<IDailyKLineSource>().Should().BeNull("内蔵 paper は OpenD を持たない");
        var res = await client.GetAsync(OkPath, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        body["status"]!.GetValue<int>().Should().Be((int)DailyBarsStatus.Unavailable);
        body["unavailableReason"]!.GetValue<string>().Should().Be(DailyBarsUnavailableReasons.BrokerNotSupported);
    }

    // moomoo 構成では相場だけのクライアントを遅延生成するポートが登録される（組み立ての時点では OpenD へ繋がない）。
    [Fact]
    public async Task moomoo構成の本番の組み立てでは日足のポートを登録する()
    {
        await using var baseFactory = new CompositionWiringGuardTests.MoomooFactory();
        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            // 自前の常駐（ガード・約定追跡など）は起動させない（伝送の偽物を巡回で叩かせない）。
            var production = typeof(Program).Assembly;
            foreach (var d in services.Where(d => d.ServiceType == typeof(IHostedService)
                         && (d.ImplementationType ?? d.ImplementationFactory?.Method.DeclaringType ?? d.ImplementationInstance?.GetType())?.Assembly == production)
                         .ToList())
                services.Remove(d);
        }));

        factory.Services.GetService<IDailyKLineSource>().Should().BeOfType<MoomooDailyKLineSource>();
    }
}
