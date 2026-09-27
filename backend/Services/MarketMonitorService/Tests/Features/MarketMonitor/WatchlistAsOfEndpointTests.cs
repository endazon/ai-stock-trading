using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using MarketMonitorService.Domain;
using MarketMonitorService.Features.MarketMonitor;
using MarketMonitorService.Features.MarketMonitor.GetWatchlistAsOf;
using MarketMonitorService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketMonitorService.Tests;

// 🔴 T-10-1626, FR-04, FR-13, FR-15, ADR-0044 決定 3, ADR-0046 決定 1, #1049, IADR-0442 決定 1・2: 当時の監視銘柄の照会
// （GET /monitor/watchlist/as-of）は**読み取り専用**で、s2s（trading-service）が読める。照会しても設定の行も履歴も書かれない。
// 変更と履歴の口の認可（OwnerOnly）は変わらない。本物の書き手（MonitorWatchlistService）が書いた前後値を読み戻せることもここで固定する。
public class WatchlistAsOfEndpointTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static HttpClient Client(MonitorWorkerWebApplicationFactory factory, string? roles)
    {
        var client = factory.CreateClient();
        if (roles is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    private static string Iso(DateTimeOffset at) => Uri.EscapeDataString(at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"));

    // 行を確実に作ってから SeededAt を与え、本物の書き手で監視銘柄を 1 件足す。
    private static DateTimeOffset Arrange(MonitorWorkerWebApplicationFactory factory, DateTimeOffset seededAt)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMonitoredSymbolStore>().GetSettings();
        var db = scope.ServiceProvider.GetRequiredService<MarketMonitorDbContext>();
        var row = db.MonitorSettings.Single(r => r.Id == SingletonKeys.Id);
        row.SeededAt = seededAt;
        db.SaveChanges();

        scope.ServiceProvider.GetRequiredService<MonitorWatchlistService>()
            .Add("META", Market.UnitedStates, "test-owner", "当時の監視銘柄の照会の試験");
        return DateTimeOffset.UtcNow;
    }

    [Fact]
    public async Task サービスロールは当時の監視銘柄を読め本物の書き手の前後値を読み戻せる()
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();
        var seeded = DateTimeOffset.UtcNow.AddDays(-3);
        var afterAdd = Arrange(factory, seeded);

        var res = await Client(factory, "trading-service").GetAsync($"/monitor/watchlist/as-of?at={Iso(afterAdd)}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await res.Content.ReadFromJsonAsync<WatchlistAsOfResponse>(Web))!;
        body.Reconstructed.Should().BeTrue(body.Reason);
        body.Symbols.Should().ContainSingle(s => s.Symbol == "META" && s.Market == Market.UnitedStates);
        body.Basis.Should().Be(WatchlistAsOfResponse.Bases.AfterChange);
        body.SeededAt.Should().BeCloseTo(seeded, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task SeededAtより前は200で再構成できないと答えSeededAtを返す()
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();
        var seeded = DateTimeOffset.UtcNow.AddDays(-3);
        Arrange(factory, seeded);

        var res = await Client(factory, "trading-service").GetAsync($"/monitor/watchlist/as-of?at={Iso(seeded.AddDays(-1))}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await res.Content.ReadFromJsonAsync<WatchlistAsOfResponse>(Web))!;
        body.Reconstructed.Should().BeFalse();
        body.Symbols.Should().BeNull();
        body.Reason.Should().NotBeNullOrWhiteSpace();
        body.SeededAt.Should().BeCloseTo(seeded, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task 利用者も読める()
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();
        var afterAdd = Arrange(factory, DateTimeOffset.UtcNow.AddDays(-1));

        var res = await Client(factory, "trading-owner").GetAsync($"/monitor/watchlist/as-of?at={Iso(afterAdd)}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("viewer", HttpStatusCode.Forbidden)]
    public async Task 未認証は401で無権限は403(string? roles, HttpStatusCode expected)
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();

        var res = await Client(factory, roles).GetAsync($"/monitor/watchlist/as-of?at={Iso(DateTimeOffset.UtcNow)}");

        res.StatusCode.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?at=")]
    [InlineData("?at=not-a-time")]
    [InlineData("?at=2026-09-25T18:09:00")] // オフセットが無い（サーバの地方時として読まれ得る）
    public async Task 時刻が無いか解釈できなければ400(string query)
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();

        var res = await Client(factory, "trading-service").GetAsync($"/monitor/watchlist/as-of{query}");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // 🔴 読み取り専用: GET 以外の方法は無い（書く経路を持たない）。
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task GET以外の方法は受け付けない(string method)
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();
        var request = new HttpRequestMessage(new HttpMethod(method), $"/monitor/watchlist/as-of?at={Iso(DateTimeOffset.UtcNow)}")
        {
            Content = JsonContent.Create(new { symbol = "AAPL", market = 1, reason = "書けてはならない" }),
        };

        var res = await Client(factory, "trading-owner").SendAsync(request);

        res.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    // 🔴 照会しても設定の行（Version・SeededAt）も履歴も変わらない。
    [Fact]
    public async Task 照会しても設定の行も履歴も書かれない()
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();
        var afterAdd = Arrange(factory, DateTimeOffset.UtcNow.AddDays(-1));
        (int Version, DateTimeOffset? SeededAt, int History) Snapshot()
        {
            using var scope = factory.Services.CreateScope();
            var row = scope.ServiceProvider.GetRequiredService<MarketMonitorDbContext>()
                .MonitorSettings.AsNoTracking().Single(r => r.Id == SingletonKeys.Id);
            var history = scope.ServiceProvider.GetRequiredService<IMonitorSettingsChangeLog>().GetHistory().Count;
            return (row.Version, row.SeededAt, history);
        }

        var before = Snapshot();
        var client = Client(factory, "trading-service");
        foreach (var at in new[] { afterAdd, afterAdd.AddDays(-2), afterAdd.AddYears(-1) })
            (await client.GetAsync($"/monitor/watchlist/as-of?at={Iso(at)}")).StatusCode.Should().Be(HttpStatusCode.OK);

        Snapshot().Should().Be(before);
    }

    // 履歴そのもの（変更者・理由）の照会は、サービスには開かないまま（OwnerOnly）。
    [Fact]
    public async Task 履歴の照会はサービスには403のまま()
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();

        var res = await Client(factory, "trading-service").GetAsync("/monitor/watchlist/history");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // 🔴 T-10-1626: seed の時刻を読むポートは何も書かない。行が無ければ null を返し、行を作らない。空の行を再 seed しない。
    [Fact]
    public void seedの時刻を読むポートは行を作らず再seedもしない()
    {
        var options = new DbContextOptionsBuilder<MarketMonitorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        using (var db = new MarketMonitorDbContext(options))
        {
            new EfMonitorSeedRecord(db).Read().Should().BeNull();
            db.MonitorSettings.Count().Should().Be(0, "読むだけで行を作ってはならない");
        }

        var seededAt = new DateTimeOffset(2026, 9, 15, 16, 30, 52, TimeSpan.Zero);
        using (var db = new MarketMonitorDbContext(options))
        {
            // 空・ClearedByUserAt なし＝構成 seed があれば GetSettings が再 seed し得る状態の行を、本物のストアで作る。
            new EfMonitoredSymbolStore(db).GetSettings();
            var row = db.MonitorSettings.Single();
            row.SeededAt = seededAt;
            row.Version = 7;
            db.SaveChanges();
        }

        using (var db = new MarketMonitorDbContext(options))
        {
            var state = new EfMonitorSeedRecord(db).Read();
            state.Should().NotBeNull();
            state!.SeededAt.Should().Be(seededAt);
            state.CurrentSymbols.Should().BeEmpty();
        }

        using (var db = new MarketMonitorDbContext(options))
        {
            var row = db.MonitorSettings.Single();
            row.Version.Should().Be(7);
            row.SeededAt.Should().Be(seededAt);
        }
    }

    // 🔴 T-10-1627, IADR-0420: 本物の Program.cs の本文は、応答型を web 既定（camelCase・列挙は数値）で直列化したものと一字一句同じ。
    // 受け手（取引判断 T-10-1629）はこの設定で直列化した本文を読ませている。
    [Fact]
    public async Task 当時の監視銘柄の本文は応答型を_web_既定で直列化したものと同じ()
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();
        var afterAdd = Arrange(factory, DateTimeOffset.UtcNow.AddDays(-1));

        var res = await Client(factory, "trading-service").GetAsync($"/monitor/watchlist/as-of?at={Iso(afterAdd)}");
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsObject();

        WatchlistAsOfResponse expected;
        using (var scope = factory.Services.CreateScope())
        {
            var truncated = DateTimeOffset.Parse(Uri.UnescapeDataString(Iso(afterAdd)));
            expected = WatchlistAsOfReconstructor.Reconstruct(
                truncated, DateTimeOffset.UtcNow,
                scope.ServiceProvider.GetRequiredService<IMonitorSettingsChangeLog>().GetHistory(),
                scope.ServiceProvider.GetRequiredService<IMonitorSeedRecord>().Read());
        }

        expected.Reconstructed.Should().BeTrue("空振りの一致は何も証明しない");
        JsonNode.DeepEquals(body, JsonSerializer.SerializeToNode(expected, Web))
            .Should().BeTrue($"as-of の本文が web 既定と異なる: {body.ToJsonString()}");
        body["reconstructed"]!.GetValue<bool>().Should().BeTrue();
        body["seededAt"].Should().NotBeNull();
        var meta = body["symbols"]!.AsArray().Single(n => n!["symbol"]!.GetValue<string>() == "META")!;
        meta["market"]!.GetValue<int>().Should().Be((int)Market.UnitedStates);
    }
}
