using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Security.Claims;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using Xunit;

namespace AiStockTrading.Bff.Endpoints.Tests;

// FR-14, IADR-0063 / IADR-0091: unit-owned BFF エンドポイント（Assumptions/RiskControls/Monitor）の
// pass-through 振る舞いを AST 単独で固定する。MSP の Platform.Bff.Tests と同趣旨だが、AST 側だけで
// このプロジェクトが変更された場合の回帰を AST 自身の CI（test）で検知するためのバックストップ。
// 後段は StubHandler で差し替え、実サービスに依存しない。
public class BffPassThroughTests
{
    // すべての BFF ルート（3 モジュール・計 18 ルート）が期待どおり登録され、グループが認証必須であることの
    // 最小固定（ルート脱落・改名・メソッド変更・RequireAuthorization 抜けを検知）。
    public static IEnumerable<object[]> AllRoutes =>
    [
        ["GET", "/bff/assumptions"],
        ["GET", "/bff/assumptions/history"],
        ["PUT", "/bff/assumptions"],
        ["GET", "/bff/risk-controls/settings"],
        ["GET", "/bff/risk-controls/settings/history"],
        ["PUT", "/bff/risk-controls/settings/limits"],
        ["PUT", "/bff/risk-controls/settings/guard"],
        // AST #334, FR-20: 発注先の変更（SC-02 だけが持つ操作）。
        ["PUT", "/bff/risk-controls/settings/broker-provider"],
        // AST #423, FR-20, SC-02: Stage 1 の最小取引件数（既定 100・値域 1〜1000）。
        ["PUT", "/bff/risk-controls/settings/stage1-minimum-trade-count"],
        ["GET", "/bff/risk-controls/status"],
        ["GET", "/bff/risk-controls/stage-gate"],
        ["GET", "/bff/monitor/watchlist"],
        ["POST", "/bff/monitor/watchlist"],
        ["DELETE", "/bff/monitor/watchlist"],
        ["GET", "/bff/monitor/watchlist/history"],
        // AST #423, FR-03, FR-13, SC-02: 市場監視パラメータ（変動閾値・クールダウン）。
        // **SC-01 §2 の廃止に伴い SC-02 が消費する。** 登録漏れのままだと画面から到達できない
        // （#340 で SC-01 §2 が `/monitor/settings` を叩き始めたのに BFF が未結線だった＝AST/IADR-0164）。
        ["GET", "/bff/monitor/settings"],
        ["PUT", "/bff/monitor/settings/movement-threshold"],
        ["PUT", "/bff/monitor/settings/cooldown"],
        ["GET", "/bff/monitor/settings/history"],
    ];

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task Anonymous_request_is_rejected_with_401(string method, string path)
    {
        await using var host = await BffTestHost.StartAsync();
        // 匿名（X-Test-User なし）はグループの RequireAuthorization により 401。
        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        var resp = await host.Client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // AST #464, FR-19, UC-06, ADR-0028 決定3, IADR-0182:
    // **実際に登録されているルートの集合が AllRoutes と完全一致すること**を固定する。
    //
    // `AllRoutes` の Theory は「列挙したルートが 401 を返す」ことしか主張せず、**ホワイトリストに
    // 無いルートが増えても赤くならない**。ADR-0028 決定3 は「解除の窓口は Discord Bot とし、
    // **画面（SC-02 / SC-03）からは解除できない**」と定めており、その回帰ガードは
    // 「BFF に経路が生えていないこと」でしか作れない —— 実測で「今は無い」と言うだけでは、
    // 次に誰かが足したときに誰も気づかない。
    //
    // **本テストが赤くなったら**、増えたルートが画面に開いてよいものかを ADR で確かめること
    // （とくに破壊的統制操作は Discord Bot へ一元化する方針である）。
    [Fact]
    public async Task 登録されている_BFF_ルートは_AllRoutes_と完全一致する()
    {
        await using var host = await BffTestHost.StartAsync();

        var actual = host.App.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                // MapGroup のプレフィックス＋空ルートは末尾 "/" を生むため正規化して比較する
                // （表記の揺れであってルートの増減ではない）。
                .Select(m => $"{m} /{(e.RoutePattern.RawText ?? string.Empty).Trim('/')}"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var expected = AllRoutes
            .Select(r => $"{r[0]} /{((string)r[1]).Trim('/')}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        actual.Should().BeEquivalentTo(expected,
            "BFF のルートが増減したら、画面へ開いてよい経路かを ADR で確かめる必要がある"
            + "（ADR-0028 決定3: 破壊的統制操作は Discord Bot へ一元化し画面からは行わない）");
    }

    // **否定形**: GFV 解除の経路が BFF に存在しない（ADR-0028 決定3 の名指しの回帰ガード）。
    // 上の完全一致テストがあれば冗長に見えるが、**こちらは何を守っているかが名前で分かる** ——
    // 完全一致テストが将来ホワイトリストごと更新されたとき、この 1 本だけは意図を主張し続ける。
    [Fact]
    public async Task GFV_解除の経路は_BFF_に存在しない()
    {
        await using var host = await BffTestHost.StartAsync();

        host.App.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .Should().NotContain(p => p.Contains("good-faith-violation", StringComparison.OrdinalIgnoreCase),
                "ADR-0028 決定3: 解除の窓口は Discord Bot であり、画面（SC-02 / SC-03）からは解除できない");
    }

    [Fact]
    public async Task Authenticated_request_passes_through_downstream_status_and_body()
    {
        await using var host = await BffTestHost.StartAsync();
        host.Downstream.ResponseBody = """{"version":7,"value":"x"}""";
        host.Downstream.Status = HttpStatusCode.OK;

        var resp = await host.SendAuthed(HttpMethod.Get, "/bff/assumptions");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Be("""{"version":7,"value":"x"}""");
        // 後段へ利用者トークンが伝播される。
        host.Downstream.LastRequest!.Headers.Authorization.Should().NotBeNull();
        // BFF プレフィックスを剥がした後段パスへ転送される。
        host.Downstream.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/assumptions");
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    public async Task Downstream_4xx_is_passed_through_unchanged(int status)
    {
        await using var host = await BffTestHost.StartAsync();
        host.Downstream.Status = (HttpStatusCode)status;

        var resp = await host.SendAuthed(HttpMethod.Put, "/bff/risk-controls/settings/limits", """{"maxDrawdown":0.1}""");

        ((int)resp.StatusCode).Should().Be(status);
    }

    [Fact]
    public async Task Downstream_unreachable_degrades_to_502()
    {
        await using var host = await BffTestHost.StartAsync();
        host.Downstream.Throw = true;

        var resp = await host.SendAuthed(HttpMethod.Get, "/bff/risk-controls/status");

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task Delete_forwards_request_body_to_downstream()
    {
        await using var host = await BffTestHost.StartAsync();
        const string body = """{"symbol":"7203","market":"TSE","reason":"監視終了"}""";

        var resp = await host.SendAuthed(HttpMethod.Delete, "/bff/monitor/watchlist", body);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // IADR-0072 決定3: DELETE /monitor/watchlist は本文（銘柄・理由）を後段へ転送する。
        host.Downstream.LastRequestBody.Should().Be(body);
        host.Downstream.LastRequest!.Method.Should().Be(HttpMethod.Delete);
        host.Downstream.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/monitor/watchlist");
    }
}

// 3 モジュールを最小ホストへ map したテストサーバ。後段は StubHandler で差し替える。
internal sealed class BffTestHost : IAsyncDisposable
{
    public required WebApplication App { get; init; }
    public required HttpClient Client { get; init; }
    public required StubHandler Downstream { get; init; }

    public static async Task<BffTestHost> StartAsync()
    {
        var downstream = new StubHandler();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(downstream));
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddRouting();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAssumptionsBffEndpoints();
        app.MapRiskControlsBffEndpoints();
        app.MapMonitorBffEndpoints();

        await app.StartAsync();
        return new BffTestHost { App = app, Client = app.GetTestClient(), Downstream = downstream };
    }

    // 認証済み（Bearer トークン付き）でリクエストする。body を渡すと JSON 本文を付ける。
    // Authorization ヘッダが認証（TestAuthHandler）と後段への伝播（ProxyAsync）の両方を駆動する。
    public Task<HttpResponseMessage> SendAuthed(HttpMethod method, string path, string? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return Client.SendAsync(req);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
    }
}

// 後段サービスを差し替えるスタブ。全名前付きクライアントを 1 つの handler に集約する。
internal sealed class StubHttpClientFactory(StubHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        new(handler, disposeHandler: false) { BaseAddress = new Uri("http://downstream.test") };
}

// 後段の応答・不達を制御し、転送されたリクエスト（本文・パス・メソッド）を捕捉する。
internal sealed class StubHandler : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public string ResponseBody { get; set; } = """{"ok":true}""";
    public bool Throw { get; set; }
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content is not null)
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
        if (Throw)
            throw new HttpRequestException("downstream unreachable");
        return new HttpResponseMessage(Status)
        {
            Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json"),
        };
    }
}

// テスト用認証スキーム: X-Test-User ヘッダがあれば認証成功、無ければ NoResult（→ RequireAuthorization が 401）。
internal sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Authorization ヘッダが無ければ未認証（→ RequireAuthorization が 401）。owner 判定は本 BFF では行わず後段委譲。
        if (!Request.Headers.ContainsKey("Authorization"))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "user-1")], "Test");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), "Test");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
