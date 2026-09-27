using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuditService.Tests;

// テスト用認証ハンドラ（RiskManagement.Worker.Tests 準拠）。JWT/Keycloak に依存せず ClaimsPrincipal を注入する。
// ロールはヘッダ "X-Test-Roles"（カンマ区切り）で指定する。
//   - ヘッダ無し          → 未認証（NoResult）→ OwnerOnly は 401
//   - "X-Test-Roles: a,b" → 指定ロールで認証（trading-owner を含めば OwnerOnly を通過、含まなければ 403）
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string RolesHeader = "X-Test-Roles";

    // NFR-06, IADR-0448, #1067: 呼び出し元のクライアント（`azp` クレーム）を模す任意のヘッダ。無ければ azp 無し（従来どおり）。
    public const string AzpHeader = "X-Test-Azp";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(RolesHeader, out var header))
            return Task.FromResult(AuthenticateResult.NoResult()); // 未認証 → 401

        var roles = header.ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var claims = new List<Claim> { new(ClaimTypes.Name, "test-owner") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        if (Request.Headers.TryGetValue(AzpHeader, out var azps))
            claims.AddRange(azps.Where(a => !string.IsNullOrEmpty(a)).Select(a => new Claim("azp", a!)));

        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
