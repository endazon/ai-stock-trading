using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderExecutionService.Tests;

// FR-10, #967: テスト用認証ハンドラ（他サービスの TestAuthHandler と同じ形）。JWT/Keycloak に依存せず ClaimsPrincipal を注入する。
// ロールはヘッダ "X-Test-Roles"（カンマ区切り）で指定する。ヘッダ無し → 未認証（NoResult）→ 認可を要する口は 401。
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string RolesHeader = "X-Test-Roles";

    // NFR-06, IADR-0448, IADR-0559, #1391: 呼び出し元のクライアント（`azp` クレーム）を模す任意のヘッダ（gRPC 面の門の試験用）。無ければ azp 無し（従来どおり）。
    public const string AzpHeader = "X-Test-Azp";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(RolesHeader, out var header))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new(ClaimTypes.Name, "test-service") };
        claims.AddRange(header.ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => new Claim(ClaimTypes.Role, r)));
        if (Request.Headers.TryGetValue(AzpHeader, out var azps))
            claims.AddRange(azps.Where(a => !string.IsNullOrEmpty(a)).Select(a => new Claim("azp", a!)));

        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
