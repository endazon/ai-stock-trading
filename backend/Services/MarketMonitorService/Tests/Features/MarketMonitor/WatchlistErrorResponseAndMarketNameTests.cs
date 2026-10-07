using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using MarketMonitorService.Domain;
using MarketMonitorService.Features.MarketMonitor;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketMonitorService.Tests;

// NFR-06, NFR-05, FR-13, SC-02, UC-06, IADR-0496, #1192: 代表サービス（市場監視）の実 Program.cs で、
// ①未処理例外の応答が ProblemDetails で Authorization の値・例外メッセージ・スタックを含まないこと（配備の環境 Production と、
//   開発者向けページが自動で挿入される Development の両方）、②監視銘柄の要求の market が列挙名の文字列も受けることを固定する。
public class WatchlistErrorResponseAndMarketNameTests
{
    private const string SecretInMessage = "store-internal-detail-marker";

    // 実行時に組み立てる非秘密の目印（トークンに見える文字列をソースへ置かない）。
    private static string AuthorizationMarker() => "Bearer " + "placeholder-" + Guid.NewGuid().ToString("N");

    private sealed class ThrowingStore : IMonitoredSymbolStore
    {
        public MarketMonitorSettings GetSettings() => throw new InvalidOperationException(SecretInMessage);

        public void Save(MarketMonitorSettings settings) => throw new InvalidOperationException(SecretInMessage);
    }

    // T-10-2353: 監視銘柄の読み取りが未処理例外を投げても、応答は ProblemDetails で Authorization・例外メッセージ・スタックを返さない。
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task 未処理例外の応答は_ProblemDetails_で要求ヘッダーとスタックを含まない(string environment)
    {
        await using var baseFactory = new MonitorWorkerWebApplicationFactory();
        await using var factory = baseFactory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            b.ConfigureTestServices(s =>
            {
                foreach (var d in s.Where(d => d.ServiceType == typeof(IMonitoredSymbolStore)).ToList()) s.Remove(d);
                s.AddScoped<IMonitoredSymbolStore, ThrowingStore>();
            });
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        var marker = AuthorizationMarker();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", marker);

        var res = await client.GetAsync("/monitor/watchlist", TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        res.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        body.Should().Contain("\"status\":500");
        body.Should().NotContain(marker["Bearer ".Length..]);
        body.Should().NotContain("Authorization");
        body.Should().NotContain(SecretInMessage);
        body.Should().NotContain(nameof(InvalidOperationException));
        body.Should().NotContain("   at ");
    }

    private static HttpClient OwnerClient(MonitorWorkerWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonArray> WatchlistAsync(HttpClient client) =>
        JsonNode.Parse(await client.GetStringAsync("/monitor/watchlist", TestContext.Current.CancellationToken))!.AsArray();

    // T-10-2354: market は列挙名の文字列（大小無視）も数値も受ける。読み取り口の列挙は数値のまま（T-10-931 の契約）。DELETE も文字列を受ける。
    [Fact]
    public async Task market_は列挙名の文字列も数値も受け_読み取りは数値のまま()
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();
        var client = OwnerClient(factory);
        var ct = TestContext.Current.CancellationToken;

        (await client.PostAsync("/monitor/watchlist",
            Json("{\"symbol\":\"ZZNAME\",\"market\":\"UnitedStates\",\"reason\":\"文字列\"}"), ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/monitor/watchlist",
            Json("{\"symbol\":\"ZZLOWER\",\"market\":\"unitedstates\",\"reason\":\"小文字\"}"), ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/monitor/watchlist",
            Json("{\"symbol\":\"ZZNUM\",\"market\":1,\"reason\":\"数値\"}"), ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await WatchlistAsync(client);
        foreach (var symbol in new[] { "ZZNAME", "ZZLOWER", "ZZNUM" })
        {
            var row = list.Single(n => n!["symbol"]!.GetValue<string>() == symbol)!;
            row["market"]!.GetValueKind().Should().Be(System.Text.Json.JsonValueKind.Number);
            row["market"]!.GetValue<int>().Should().Be(1);
        }

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/monitor/watchlist")
        {
            Content = Json("{\"symbol\":\"ZZNAME\",\"market\":\"UnitedStates\",\"reason\":\"文字列で削除\"}"),
        };
        (await client.SendAsync(delete, ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await WatchlistAsync(client)).Should().NotContain(n => n!["symbol"]!.GetValue<string>() == "ZZNAME");
    }

    // T-10-2355: 未知の市場名は 400 で、監視銘柄へ足さない（否定形）。
    [Fact]
    public async Task 未知の市場名は_400_で追加しない()
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();
        var client = OwnerClient(factory);
        var ct = TestContext.Current.CancellationToken;

        var res = await client.PostAsync("/monitor/watchlist",
            Json("{\"symbol\":\"ZZMARS\",\"market\":\"Mars\",\"reason\":\"未知\"}"), ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await WatchlistAsync(client)).Should().NotContain(n => n!["symbol"]!.GetValue<string>() == "ZZMARS");
    }

    // T-10-2364: market の文字列は列挙名そのもの（大小無視）だけを受ける。ビット和（"Japan, UnitedStates"）・前後の空白・数字の文字列は
    // 400 で、監視銘柄へ足さない（否定形）。数値の扱いは従来どおり（1 は受け、未定義の 99・null・省略は 400）。
    [Theory]
    [InlineData("\"Japan, UnitedStates\"", "ZZCOMMA")]
    [InlineData("\"Japan,UnitedStates\"", "ZZCOMMA2")]
    [InlineData("\" UnitedStates\"", "ZZPADL")]
    [InlineData("\"UnitedStates \"", "ZZPADR")]
    [InlineData("\"1\"", "ZZDIGIT")]
    [InlineData("\"\"", "ZZEMPTY")]
    [InlineData("99", "ZZUNDEF")]
    [InlineData("1.5", "ZZFRAC")]
    [InlineData("null", "ZZNULL")]
    [InlineData("true", "ZZBOOL")]
    public async Task market_の曖昧な文字列と未定義の値は_400_で追加しない(string marketJson, string symbol)
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();
        var client = OwnerClient(factory);
        var ct = TestContext.Current.CancellationToken;

        var res = await client.PostAsync("/monitor/watchlist",
            Json($"{{\"symbol\":\"{symbol}\",\"market\":{marketJson},\"reason\":\"厳格\"}}"), ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await WatchlistAsync(client)).Should().NotContain(n => n!["symbol"]!.GetValue<string>() == symbol);
    }

    // T-10-2364（陽性対照）: 数値 0 / 1 と列挙名（大小無視）は受け、省略は 400（従来どおり）。
    [Fact]
    public async Task market_の数値と列挙名は従来どおり受け_省略は_400()
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();
        var client = OwnerClient(factory);
        var ct = TestContext.Current.CancellationToken;

        (await client.PostAsync("/monitor/watchlist",
            Json("{\"symbol\":\"ZZZERO\",\"market\":0,\"reason\":\"数値0\"}"), ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/monitor/watchlist",
            Json("{\"symbol\":\"ZZJAPAN\",\"market\":\"JAPAN\",\"reason\":\"大文字\"}"), ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/monitor/watchlist",
            Json("{\"symbol\":\"ZZOMIT\",\"reason\":\"省略\"}"), ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var list = await WatchlistAsync(client);
        list.Single(n => n!["symbol"]!.GetValue<string>() == "ZZZERO")!["market"]!.GetValue<int>().Should().Be(0);
        list.Single(n => n!["symbol"]!.GetValue<string>() == "ZZJAPAN")!["market"]!.GetValue<int>().Should().Be(0);
        list.Should().NotContain(n => n!["symbol"]!.GetValue<string>() == "ZZOMIT");
    }
}
