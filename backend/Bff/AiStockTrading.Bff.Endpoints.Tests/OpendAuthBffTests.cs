using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Xunit;

namespace AiStockTrading.Bff.Endpoints.Tests;

// SC-04, FR-09, FR-11, UC-06（代替フロー「ゲートウェイの有人認証」）, NFR-05, NFR-06,
// ADR-0002 前提条件1, ADR-0024 決定1・2, IADR-0321, planning#594:
// OpenD 認証操作（SC-04）の BFF の振る舞いを固定する。
//
// **受け入れ基準を写像した 4 本の柱**（依頼の「Tests」節）:
//   ① `trading-owner` だけが到達する（他は存在秘匿の 404・**上流を呼ばない**）
//   ② 検証コードが**応答にもログにも 1 度も現れない**
//   ③ 待機中でなければ **409**（上流へ 1 バイトも送らない）
//   ④ **クライアントは送信コマンドを左右できない**
public class OpendAuthBffTests
{
    private const string Code = "483921";

    public static IEnumerable<object[]> OpendAuthRoutes =>
    [
        ["GET", "/bff/opend-auth/state"],
        ["GET", "/bff/opend-auth/captcha"],
        ["POST", "/bff/opend-auth/code"],
        ["POST", "/bff/opend-auth/resend"],
    ];

    // ---- ① 認可（trading-owner 限定・存在秘匿） ----

    // 🔴 **403 ではなく 404 である。** 403 は「在るが権限が無い」と告げてしまう（NFR-06 の存在秘匿）。
    [Theory]
    [MemberData(nameof(OpendAuthRoutes))]
    public async Task 認証済みでもロールが無ければ存在秘匿の404を返す(string method, string path)
    {
        await using var host = await OpendAuthTestHost.StartAsync();

        var resp = await host.SendAsync(method, path, roles: "some-other-role", body: """{"code":"483921"}""");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 🔴 **否定形**: 権限外では上流を呼ばない。呼ぶと応答時間の差だけで端点の存在が分かるうえ、
    // 認証を持たない上流（OpenD サイドカー）へ無権限の要求が届いてしまう。
    [Theory]
    [MemberData(nameof(OpendAuthRoutes))]
    public async Task 権限外のときは上流を1度も呼ばない(string method, string path)
    {
        await using var host = await OpendAuthTestHost.StartAsync();

        await host.SendAsync(method, path, roles: "some-other-role", body: """{"code":"483921"}""");

        host.Upstream.Requests.Should().BeEmpty("権限外の要求が認証を持たない上流へ届いてはならない");
    }

    [Theory]
    [MemberData(nameof(OpendAuthRoutes))]
    public async Task 匿名は401で拒否される(string method, string path)
    {
        await using var host = await OpendAuthTestHost.StartAsync();

        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        var resp = await host.Client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task trading_owner_は状態を取得できる()
    {
        await using var host = await OpendAuthTestHost.StartAsync();
        host.Upstream.State = Waiting("phone");

        var resp = await host.SendAsync("GET", "/bff/opend-auth/state", roles: OpendAuthTestHost.OwnerRole);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await ReadStateAsync(resp);
        view.GetProperty("prompt").GetString().Should().Be("phone");
        view.GetProperty("promptAvailability").GetInt32().Should().Be(0);
    }

    // ---- ② 検証コードを記録しない（NFR-05 の適用範囲の拡張） ----

    // 🔴 本画面で最も高くつく漏洩である。**投入したコードは、応答にもログにも一切現れてはならない。**
    // 監査ログ・送信履歴・Discord 通知にも残さないことを計画が定めており、BFF はその最初の関門である。
    [Fact]
    public async Task 検証コードは応答本文にもログにも現れない()
    {
        await using var host = await OpendAuthTestHost.StartAsync();
        host.Upstream.State = Waiting("phone");
        host.Upstream.VerifyStatus = HttpStatusCode.NoContent;

        var resp = await host.SendAsync(
            "POST", "/bff/opend-auth/code", roles: OpendAuthTestHost.OwnerRole, body: $$"""{"code":"{{Code}}"}""");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await resp.Content.ReadAsStringAsync()).Should().NotContain(Code);
        host.Logs.Should().NotContain(l => l.Contains(Code, StringComparison.Ordinal),
            "検証コードは 1 つでゲートウェイのログインが通る値であり、ログに残してはならない");
    }

    // 上流が拒否（400）したときも、**何が違ったかを返さない**（入力を反射しない）。
    [Fact]
    public async Task 上流が拒否してもコードを反射しない()
    {
        await using var host = await OpendAuthTestHost.StartAsync();
        host.Upstream.State = Waiting("phone");
        host.Upstream.VerifyStatus = HttpStatusCode.BadRequest;
        // 🔴 上流が将来コードをエコーし始めても、BFF は本文を捨てるので漏れない。
        host.Upstream.VerifyBody = $$"""{"error":"mismatch","submitted":"{{Code}}"}""";

        var resp = await host.SendAsync(
            "POST", "/bff/opend-auth/code", roles: OpendAuthTestHost.OwnerRole, body: $$"""{"code":"{{Code}}"}""");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().NotContain(Code);
        host.Logs.Should().NotContain(l => l.Contains(Code, StringComparison.Ordinal));
    }

    // 壊れた本文（JSON として解釈できない）でも、例外メッセージ経由でコードが漏れない。
    // `JsonException.Message` には本文の断片が入るため、握り潰し方を誤ると簡単に漏れる。
    [Fact]
    public async Task 壊れた本文でも例外メッセージ経由でコードが漏れない()
    {
        await using var host = await OpendAuthTestHost.StartAsync();
        host.Upstream.State = Waiting("phone");

        var resp = await host.SendAsync(
            "POST", "/bff/opend-auth/code", roles: OpendAuthTestHost.OwnerRole, body: $$"""{"code":"{{Code}}" """);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().NotContain(Code);
        host.Logs.Should().NotContain(l => l.Contains(Code, StringComparison.Ordinal));
    }

    // ---- ③ 待機中でなければ 409 ----

    [Fact]
    public async Task 入力を待っていないときは409で拒否し上流へ送らない()
    {
        await using var host = await OpendAuthTestHost.StartAsync();
        host.Upstream.State = """{"status":"idle","prompt":null,"captchaAvailable":false,"lastLoginAt":"2026-09-09T21:02:00Z","detail":null}""";

        var resp = await host.SendAsync(
            "POST", "/bff/opend-auth/code", roles: OpendAuthTestHost.OwnerRole, body: $$"""{"code":"{{Code}}"}""");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        // 🔴 **画面の無効化だけに頼らない**（計画 05_screens SC-04）。上流へ 1 バイトも送らない。
        host.Upstream.Requests.Should().NotContain(r => r.Path.Contains("verify", StringComparison.Ordinal));
    }

    // 上流が「待機中」と言いながらプロンプト種別を返せないときも、**送らない**。
    // 何を入力すべきか判らない状態で投入すると、誤ったコマンドへ流れ得る。
    [Fact]
    public async Task 待機中でもプロンプト種別が読めなければ送らない()
    {
        await using var host = await OpendAuthTestHost.StartAsync();
        host.Upstream.State = """{"status":"waiting","prompt":null,"captchaAvailable":false,"lastLoginAt":null,"detail":null}""";

        var resp = await host.SendAsync(
            "POST", "/bff/opend-auth/code", roles: OpendAuthTestHost.OwnerRole, body: $$"""{"code":"{{Code}}"}""");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        host.Upstream.Requests.Should().NotContain(r => r.Path.Contains("verify", StringComparison.Ordinal));
    }

    // ---- ④ クライアントは送信コマンドを左右できない ----

    // 🔴 **本画面の中核の統制。** 利用者がコマンドを選べると、`show_delay_report -detail_report_path=`
    // （root 権限で任意パスへ書ける）のような禁止コマンドへの足がかりになる。
    // ここでは「本文へ command / kind を混ぜても、上流へ渡る本文はコードだけである」ことを固定する。
    [Fact]
    public async Task 本文にコマンドを混ぜても上流へはコードだけが渡る()
    {
        await using var host = await OpendAuthTestHost.StartAsync();
        host.Upstream.State = Waiting("phone");
        host.Upstream.VerifyStatus = HttpStatusCode.NoContent;

        var resp = await host.SendAsync(
            "POST",
            "/bff/opend-auth/code",
            roles: OpendAuthTestHost.OwnerRole,
            // 攻撃者が送り得る形を丸ごと入れる。
            body: $$"""
            {"code":"{{Code}}","command":"show_delay_report -detail_report_path=/root/.com.moomoo.OpenD/Device.dat",
             "kind":"pic","prompt":"pic","cmd":"exit"}
            """);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var verify = host.Upstream.Requests.Single(r => r.Path.Contains("verify", StringComparison.Ordinal));
        var body = JsonDocument.Parse(verify.Body!).RootElement;
        // 上流へ渡るのはコードだけ——型に無い欄は構造的に落ちる。
        body.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["code"]);
        verify.Body.Should().NotContain("show_delay_report").And.NotContain("exit");
    }

    // 待機中プロンプトが変われば送信先も変わる。**決めているのはサーバである**ことの対の証明
    // （クライアントは同じ本文を送っているのに、上流の状態だけで宛先が変わる）。
    [Theory]
    [InlineData("phone")]
    [InlineData("pic")]
    public async Task 送信するコマンド種別は上流の待機プロンプトだけで決まる(string prompt)
    {
        await using var host = await OpendAuthTestHost.StartAsync();
        host.Upstream.State = Waiting(prompt);
        host.Upstream.VerifyStatus = HttpStatusCode.NoContent;

        // クライアントは常に「反対側」の種別を主張する。無視されなければならない。
        var claimed = prompt == "phone" ? "pic" : "phone";
        var resp = await host.SendAsync(
            "POST",
            "/bff/opend-auth/code",
            roles: OpendAuthTestHost.OwnerRole,
            body: $$"""{"code":"{{Code}}","kind":"{{claimed}}"}""");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        // BFF は state を読んでからコマンドを決める（読まずに投入していないこと）。
        host.Upstream.Requests.Should().Contain(r => r.Path.Contains("state", StringComparison.Ordinal));
        var verify = host.Upstream.Requests.Single(r => r.Path.Contains("verify", StringComparison.Ordinal));
        verify.Body.Should().NotContain(claimed);
    }

    // ---- 3 状態の描き分け（値あり / 対象なし / 供給が無い） ----

    // 🔴 **「いま入力を待っていない」と「状態を取得できていない」を取り違えると、
    // ゲートウェイへ到達できていないのに「正常にログインできている」ように見える。**
    [Fact]
    public async Task 入力待ちでないときは対象なしを宣言する()
    {
        await using var host = await OpendAuthTestHost.StartAsync();
        host.Upstream.State = """{"status":"idle","prompt":null,"captchaAvailable":false,"lastLoginAt":"2026-09-09T21:02:00Z","detail":null}""";

        var view = await ReadStateAsync(
            await host.SendAsync("GET", "/bff/opend-auth/state", roles: OpendAuthTestHost.OwnerRole));

        // 2 = NotApplicable（対象なし）。**1（供給が無い）ではない。**
        view.GetProperty("promptAvailability").GetInt32().Should().Be(2);
        view.GetProperty("connection").GetString().Should().Be("idle");
    }

    [Fact]
    public async Task 上流不達のときは供給が無いと宣言する()
    {
        await using var host = await OpendAuthTestHost.StartAsync();
        host.Upstream.Throw = true;

        var resp = await host.SendAsync("GET", "/bff/opend-auth/state", roles: OpendAuthTestHost.OwnerRole);

        // 宣言そのものが要るので 200 で返す（画面が 3 状態を描き分けるため）。
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await ReadStateAsync(resp);
        // 1 = NotSupplied（供給が無い）。**2（対象なし）ではない。**
        view.GetProperty("promptAvailability").GetInt32().Should().Be(1);
        view.GetProperty("connection").GetString().Should().Be("unavailable");
        view.GetProperty("captchaAvailable").GetBoolean().Should().BeFalse();
    }

    // 🔴 **未構成は「供給が無い」であって「正常」ではない。**
    // `OpendAuth:BaseUrl` の既定は空であり、空は no-op を意味する fail-safe な既定である。
    [Fact]
    public async Task 接続先が未構成のときは供給が無いと宣言し上流を呼ばない()
    {
        await using var host = await OpendAuthTestHost.StartAsync(baseUrl: "");

        var resp = await host.SendAsync("GET", "/bff/opend-auth/state", roles: OpendAuthTestHost.OwnerRole);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await ReadStateAsync(resp);
        view.GetProperty("promptAvailability").GetInt32().Should().Be(1);
        host.Upstream.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task 未構成のとき投入と再送は503へ倒れ上流を呼ばない()
    {
        await using var host = await OpendAuthTestHost.StartAsync(baseUrl: "");

        var code = await host.SendAsync(
            "POST", "/bff/opend-auth/code", roles: OpendAuthTestHost.OwnerRole, body: $$"""{"code":"{{Code}}"}""");
        var resend = await host.SendAsync("POST", "/bff/opend-auth/resend", roles: OpendAuthTestHost.OwnerRole);

        code.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        resend.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        host.Upstream.Requests.Should().BeEmpty();
    }

    // 配備構成（PVC・固定 NAT）由来の 2 指標は、上流へ到達できなくても供給され得る
    // （ADR-0024 決定1 の 2 条件。計画 05_screens SC-04「表示項目の供給元」の表）。
    [Fact]
    public async Task デバイス信頼とegress安定性は構成から供給され上流不達でも保たれる()
    {
        await using var host = await OpendAuthTestHost.StartAsync(deviceTrust: "true", egressStable: "true");
        host.Upstream.Throw = true;

        var view = await ReadStateAsync(
            await host.SendAsync("GET", "/bff/opend-auth/state", roles: OpendAuthTestHost.OwnerRole));

        view.GetProperty("deviceTrustAvailability").GetInt32().Should().Be(0);
        view.GetProperty("deviceTrustPersisted").GetBoolean().Should().BeTrue();
        view.GetProperty("egressStabilityAvailability").GetInt32().Should().Be(0);
        view.GetProperty("egressStable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task 構成が無い指標は供給が無いと宣言する()
    {
        await using var host = await OpendAuthTestHost.StartAsync();
        host.Upstream.State = Waiting("phone");

        var view = await ReadStateAsync(
            await host.SendAsync("GET", "/bff/opend-auth/state", roles: OpendAuthTestHost.OwnerRole));

        view.GetProperty("deviceTrustAvailability").GetInt32().Should().Be(1);
        view.GetProperty("egressStabilityAvailability").GetInt32().Should().Be(1);
    }

    // ---- CAPTCHA ----

    [Fact]
    public async Task CAPTCHAはimage_pngで中継され不在は404になる()
    {
        await using var host = await OpendAuthTestHost.StartAsync();
        host.Upstream.CaptchaBytes = [0x89, 0x50, 0x4E, 0x47];

        var found = await host.SendAsync("GET", "/bff/opend-auth/captcha", roles: OpendAuthTestHost.OwnerRole);
        found.StatusCode.Should().Be(HttpStatusCode.OK);
        found.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await found.Content.ReadAsByteArrayAsync()).Should().Equal(0x89, 0x50, 0x4E, 0x47);

        host.Upstream.CaptchaBytes = null;
        var missing = await host.SendAsync("GET", "/bff/opend-auth/captcha", roles: OpendAuthTestHost.OwnerRole);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- 再送 ----

    [Fact]
    public async Task 再送は本文を持たず上流の409を透過する()
    {
        await using var host = await OpendAuthTestHost.StartAsync();
        host.Upstream.ResendStatus = HttpStatusCode.NoContent;

        var ok = await host.SendAsync("POST", "/bff/opend-auth/resend", roles: OpendAuthTestHost.OwnerRole);
        ok.StatusCode.Should().Be(HttpStatusCode.NoContent);
        // 送るコマンドは `req_phone_verify_code` の 1 つに固定であり、選択肢が本文に無い。
        host.Upstream.Requests.Single(r => r.Path.Contains("resend", StringComparison.Ordinal))
            .Body.Should().BeNullOrEmpty();

        // レート制限は上流が課す（暫定 60 秒に 1 回・実測待ち）。BFF はその 409 を透過する。
        host.Upstream.ResendStatus = HttpStatusCode.Conflict;
        var limited = await host.SendAsync("POST", "/bff/opend-auth/resend", roles: OpendAuthTestHost.OwnerRole);
        limited.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- 否定形: 禁止コマンドの経路が BFF に存在しない ----

    // 🔴 計画 05_screens SC-04 が名指しで禁じた 6 コマンドの回帰ガード。
    // 「いま無い」と実測で言うだけでは、次に誰かが足したときに誰も気づかない。
    [Theory]
    [InlineData("relogin")]
    [InlineData("exit")]
    [InlineData("close-api-conn")]
    [InlineData("set-log-level")]
    [InlineData("delay-report")]
    [InlineData("sub-info")]
    [InlineData("console")]
    [InlineData("command")]
    public async Task 禁止コマンドに相当する経路はBFFに存在しない(string forbidden)
    {
        await using var host = await OpendAuthTestHost.StartAsync();

        var resp = await host.SendAsync(
            "POST", $"/bff/opend-auth/{forbidden}", roles: OpendAuthTestHost.OwnerRole, body: "{}");

        // 経路が無いので 404。**自由入力のコンソールを画面へ開かない**（計画 SC-04）。
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        host.Upstream.Requests.Should().BeEmpty();
    }

    private static string Waiting(string prompt) =>
        $$"""{"status":"waiting","prompt":"{{prompt}}","captchaAvailable":true,"lastLoginAt":"2026-09-09T21:02:00Z","detail":null}""";

    private static async Task<JsonElement> ReadStateAsync(HttpResponseMessage resp) =>
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
}

// SC-04 の BFF だけを載せたテストサーバ。上流（OpenD 認証サイドカー）はスタブへ差し替える。
// `BffTestHost`（3 モジュール用）と分けるのは、本モジュールが**ロールと構成に依存する**ためである
// （既存 3 モジュールはどちらにも依存しない）。
internal sealed class OpendAuthTestHost : IAsyncDisposable
{
    internal const string OwnerRole = "trading-owner";

    public required WebApplication App { get; init; }
    public required HttpClient Client { get; init; }
    public required OpendAuthStubHandler Upstream { get; init; }
    public required CapturingLoggerProvider LogSink { get; init; }

    /// <summary>収集したログ行（本文・引数を文字列化したもの）。</summary>
    public IReadOnlyCollection<string> Logs => LogSink.Lines;

    public static async Task<OpendAuthTestHost> StartAsync(
        string? baseUrl = "http://opend-auth.test",
        string? deviceTrust = null,
        string? egressStable = null)
    {
        var upstream = new OpendAuthStubHandler();
        var logSink = new CapturingLoggerProvider();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var settings = new Dictionary<string, string?> { ["OpendAuth:BaseUrl"] = baseUrl };
        if (deviceTrust is not null) settings["OpendAuth:DeviceTrustPersisted"] = deviceTrust;
        if (egressStable is not null) settings["OpendAuth:EgressStable"] = egressStable;
        builder.Configuration.AddInMemoryCollection(settings);

        // 🔴 ログを捨てない。**「コードがログに出ていない」ことを主張するには、まずログを集める必要がある。**
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logSink);
        builder.Logging.SetMinimumLevel(LogLevel.Trace);

        builder.Services.AddSingleton<IHttpClientFactory>(new OpendAuthStubClientFactory(upstream));
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, RoleTestAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddRouting();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOpendAuthBffEndpoints();

        await app.StartAsync();
        return new OpendAuthTestHost
        {
            App = app,
            Client = app.GetTestClient(),
            Upstream = upstream,
            LogSink = logSink,
        };
    }

    /// <summary>ロール付き（`X-Test-Roles`）でリクエストする。ロール未指定は匿名。</summary>
    public Task<HttpResponseMessage> SendAsync(string method, string path, string? roles = null, string? body = null)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (roles is not null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
            req.Headers.TryAddWithoutValidation(RoleTestAuthHandler.RolesHeader, roles);
        }
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return Client.SendAsync(req);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
        LogSink.Dispose();
    }
}

// 上流サイドカーのスタブ。パスごとに応答を出し分け、届いた要求（パス・本文）を捕捉する。
internal sealed class OpendAuthStubHandler : HttpMessageHandler
{
    public sealed record Captured(string Path, string? Body);

    public ConcurrentQueue<Captured> Captures { get; } = new();
    public IReadOnlyCollection<Captured> Requests => Captures.ToArray();

    public string State { get; set; } =
        """{"status":"unavailable","prompt":null,"captchaAvailable":false,"lastLoginAt":null,"detail":null}""";
    public HttpStatusCode VerifyStatus { get; set; } = HttpStatusCode.NoContent;
    public string? VerifyBody { get; set; }
    public HttpStatusCode ResendStatus { get; set; } = HttpStatusCode.NoContent;
    public byte[]? CaptchaBytes { get; set; } = [0x89, 0x50, 0x4E, 0x47];
    public bool Throw { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        string? body = null;
        if (request.Content is not null) body = await request.Content.ReadAsStringAsync(cancellationToken);
        Captures.Enqueue(new Captured(path, body));

        if (Throw) throw new HttpRequestException("upstream unreachable");

        if (path.EndsWith("/state", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(State, Encoding.UTF8, "application/json"),
            };
        }
        if (path.EndsWith("/captcha", StringComparison.Ordinal))
        {
            if (CaptchaBytes is null) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var content = new ByteArrayContent(CaptchaBytes);
            content.Headers.TryAddWithoutValidation("Content-Type", "image/png");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
        if (path.EndsWith("/verify", StringComparison.Ordinal))
        {
            var resp = new HttpResponseMessage(VerifyStatus);
            if (VerifyBody is not null)
                resp.Content = new StringContent(VerifyBody, Encoding.UTF8, "application/json");
            return resp;
        }
        if (path.EndsWith("/resend", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(ResendStatus);
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

internal sealed class OpendAuthStubClientFactory(OpendAuthStubHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

// ロールを `X-Test-Roles`（カンマ区切り）で与えるテスト用認証スキーム。
// 既存サービスの `TestAuthHandler` と同じ規約に揃えてある（Authorization 無し→401）。
internal sealed class RoleTestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string RolesHeader = "X-Test-Roles";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey("Authorization"))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "user-1") };
        if (Request.Headers.TryGetValue(RolesHeader, out var roles))
        {
            foreach (var role in roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
                claims.Add(new Claim(ClaimTypes.Role, role.Trim()));
        }

        var identity = new ClaimsIdentity(claims, "Test", ClaimTypes.NameIdentifier, ClaimTypes.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), "Test");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

// すべてのログ行を溜めるプロバイダ。**「出ていない」ことを主張するために要る。**
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_lines, categoryName);

    public void Dispose() { }

    private sealed class CapturingLogger(ConcurrentQueue<string> lines, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // 例外も含めて文字列化する（例外メッセージ経由の漏洩を捕まえるため）。
            lines.Enqueue($"{category}|{logLevel}|{formatter(state, exception)}|{exception}");
        }
    }
}
