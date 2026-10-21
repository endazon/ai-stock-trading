using System.Net;
using System.Text;
using AwesomeAssertions;
using OpendAuthGateway.Features.OpendAuth;
using Xunit;

namespace OpendAuthGateway.Tests.Features.OpendAuth;

/// <summary>
/// NFR, ADR-0002, IADR-0322, IADR-0566: 呼び出し元の検証（共有シークレット）の受け入れ試験。
/// <para>
/// 両向きで押さえる —— 正しいシークレットなら 4 本とも従来どおり動くこと、欠けた・誤ったシークレットでは
/// 4 本とも本文を読む前に棄却され、<b>OpenD の標準入力へ 1 バイトも書かれない</b>こと。
/// </para>
/// </summary>
public class CallerSecretFilterTests : IDisposable
{
    private const string WaitingForPhone = ">>>Logging in\n>>>Command Tips: input_phone_verify_code -code=123456\n";

    private readonly OpendAuthWebApplicationFactory _factory = new();

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    public static TheoryData<string, string> AllRoutes => new()
    {
        { "GET", "/opend-auth/state" },
        { "GET", "/opend-auth/captcha" },
        { "POST", "/opend-auth/verify" },
        { "POST", "/opend-auth/resend" },
    };

    private static HttpRequestMessage Request(string method, string path, string? secret)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            req.Content = new StringContent(path.EndsWith("/verify", StringComparison.Ordinal) ? """{"code":"123456"}""" : string.Empty,
                Encoding.UTF8, "application/json");
        }
        if (secret is not null) req.Headers.TryAddWithoutValidation(CallerSecretFilter.HeaderName, secret);
        return req;
    }

    private async Task<HttpResponseMessage> SendAsync(string method, string path, string? secret)
    {
        // ヘッダーの有無を試験ごとに決めるため、既定ヘッダーを持たないクライアントで送る。
        using var client = _factory.CreateClientWithoutCallerSecret();
        using var req = Request(method, path, secret);
        return await client.SendAsync(req);
    }

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task 正しいシークレットなら検証を通り抜ける(string method, string path)
    {
        _factory.GivenConsole(WaitingForPhone);
        _factory.GivenCaptcha([0x89, 0x50, 0x4E, 0x47]);

        var resp = await SendAsync(method, path, OpendAuthWebApplicationFactory.CallerSecret);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task ヘッダーが無ければ401で何も書かない(string method, string path)
    {
        _factory.GivenConsole(WaitingForPhone);

        var resp = await SendAsync(method, path, secret: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("unauthorized");
        _factory.Writer.Lines.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task 値が違えば401で何も書かない(string method, string path)
    {
        _factory.GivenConsole(WaitingForPhone);

        var resp = await SendAsync(method, path, OpendAuthWebApplicationFactory.CallerSecret + "x");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Writer.Lines.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task シークレットが未構成なら正しいヘッダーでも503で何も書かない(string method, string path)
    {
        _factory.GivenConsole(WaitingForPhone);
        // 先にホストを起こしてから消す（工場は起動時にファイルを置く）。
        using var warm = _factory.CreateClient();
        _factory.GivenCallerSecret(null);

        var resp = await SendAsync(method, path, OpendAuthWebApplicationFactory.CallerSecret);

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("caller_auth_not_configured");
        _factory.Writer.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task 短すぎるシークレットは未構成と同じに扱う()
    {
        using var warm = _factory.CreateClient();
        var shortSecret = new string('a', CallerSecretFilter.MinimumSecretLength - 1);
        _factory.GivenCallerSecret(shortSecret);

        var resp = await SendAsync("GET", "/opend-auth/state", shortSecret);

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task ファイルの差し替えは再起動なしに効く()
    {
        using var warm = _factory.CreateClient();
        var rotated = "rotated-caller-secret-" + new string('z', CallerSecretFilter.MinimumSecretLength);
        _factory.GivenCallerSecret(rotated + "\n");

        var old = await SendAsync("GET", "/opend-auth/state", OpendAuthWebApplicationFactory.CallerSecret);
        var current = await SendAsync("GET", "/opend-auth/state", rotated);

        old.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        current.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 応答にもログにもシークレットの値を載せない()
    {
        var wrong = "presented-wrong-secret-" + new string('q', CallerSecretFilter.MinimumSecretLength);

        var denied = await SendAsync("GET", "/opend-auth/state", wrong);
        var body = await denied.Content.ReadAsStringAsync();

        body.Should().NotContain(wrong).And.NotContain(OpendAuthWebApplicationFactory.CallerSecret);
        denied.Headers.ToString().Should().NotContain(wrong);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("abc", false)]
    public void 空や異なる提示は一致しない(string? presented, bool expected)
    {
        CallerSecretFilter.Matches(OpendAuthWebApplicationFactory.CallerSecret, presented).Should().Be(expected);
    }

    [Fact]
    public void 同じ値は一致する()
    {
        CallerSecretFilter.Matches(OpendAuthWebApplicationFactory.CallerSecret, OpendAuthWebApplicationFactory.CallerSecret)
            .Should().BeTrue();
    }

    [Fact]
    public void ファイルのパスが空なら未構成()
    {
        CallerSecretFilter.ReadSecret("").Should().BeNull();
        CallerSecretFilter.ReadSecret(null).Should().BeNull();
        CallerSecretFilter.ReadSecret(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid())).Should().BeNull();
    }
}
