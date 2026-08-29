using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RiskManagementService.Tests;

// テスト用認証ハンドラ（platform AuthorizationService.Api.Tests 準拠）。JWT/Keycloak に依存せず
// ClaimsPrincipal を注入する。ロールはヘッダ "X-Test-Roles"（カンマ区切り）で指定する。
//   - ヘッダ無し          → 未認証（NoResult）→ OwnerOnly は 401
//   - "X-Test-Roles: a,b" → 指定ロールで認証（trading-owner を含めば OwnerOnly を通過、含まなければ 403）
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string RolesHeader = "X-Test-Roles";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(RolesHeader, out var header))
            return Task.FromResult(AuthenticateResult.NoResult()); // 未認証 → 401

        var roles = header.ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var claims = new List<Claim> { new(ClaimTypes.Name, "test-owner") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
