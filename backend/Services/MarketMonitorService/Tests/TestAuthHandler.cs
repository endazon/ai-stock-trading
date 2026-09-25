using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketMonitorService.Tests;

// テスト用認証ハンドラ（リスク管理 Worker テスト準拠）。ロールはヘッダ "X-Test-Roles" で指定する。
//   - ヘッダ無し → 未認証（NoResult）→ OwnerOnly は 401
//   - "X-Test-Roles: trading-owner" → OwnerOnly 通過、他ロールは 403
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string RolesHeader = "X-Test-Roles";

    // FR-13, #1025, IADR-0433: 機密クライアントのトークン（`azp`・名前クレーム無し）を模す任意のヘッダ。無ければ従来どおり。
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
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
