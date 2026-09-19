using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReportService.Tests;

// テスト用認証ハンドラ（RiskManagement.Worker.Tests 準拠）。ロールはヘッダ "X-Test-Roles" で指定する
// （無し→401、trading-owner 含む→OwnerOnly 通過、含まない→403）。
//
// FR-09, UC-03, IADR-0240 決定11, #774: 機密クライアント（client_credentials）のトークンを模すため、
// "X-Test-Azp"（`azp` クレーム）と "X-Test-Name"（名前クレーム。**NoName＝名前クレーム無し**＝稼働環境で
// 確定者が unknown になった形。空のヘッダ値は HttpClient が送らないため番兵値で表す）を任意で受ける。どちらも無ければ従来どおり名前は test-owner・azp 無し。
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string RolesHeader = "X-Test-Roles";
    public const string AzpHeader = "X-Test-Azp";
    public const string NameHeader = "X-Test-Name";
    public const string NoName = "(none)";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(RolesHeader, out var header))
            return Task.FromResult(AuthenticateResult.NoResult());

        var roles = header.ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var name = Request.Headers.TryGetValue(NameHeader, out var nameHeader) ? nameHeader.ToString() : "test-owner";

        var claims = new List<Claim>();
        if (name != NoName)
            claims.Add(new Claim(ClaimTypes.Name, name));
        if (Request.Headers.TryGetValue(AzpHeader, out var azp) && azp.ToString().Length > 0)
            claims.Add(new Claim("azp", azp.ToString()));
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
