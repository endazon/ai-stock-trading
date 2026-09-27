using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConfigurationService.Tests;

// テスト用認証ハンドラ（RiskManagement.Worker.Tests 準拠）。JWT/Keycloak に依存せず ClaimsPrincipal を注入する。
// ロールはヘッダ "X-Test-Roles"（カンマ区切り）で指定する（無し→401、trading-owner 含む→OwnerOnly 通過、含まない→403）。
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string RolesHeader = "X-Test-Roles";

    // NFR-06, IADR-0448, #1067: 呼び出し元のクライアント（`azp` クレーム）を模す任意のヘッダ。無ければ azp 無し（従来どおり）。
    public const string AzpHeader = "X-Test-Azp";

    // #1067 監査: `client_id` クレームと名前クレームを模す任意のヘッダ（azp の代わりに照合しないことの否定の試験用）。
    public const string ClientIdHeader = "X-Test-ClientId";
    public const string NameHeader = "X-Test-Name";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(RolesHeader, out var header))
            return Task.FromResult(AuthenticateResult.NoResult());

        var roles = header.ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var name = Request.Headers.TryGetValue(NameHeader, out var nameHeader) ? nameHeader.ToString() : "test-owner";
        var claims = new List<Claim> { new(ClaimTypes.Name, name), new("preferred_username", name) };
        if (Request.Headers.TryGetValue(ClientIdHeader, out var clientId) && clientId.ToString().Length > 0)
            claims.Add(new Claim("client_id", clientId.ToString()));
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        if (Request.Headers.TryGetValue(AzpHeader, out var azps))
            claims.AddRange(azps.Where(a => !string.IsNullOrEmpty(a)).Select(a => new Claim("azp", a!)));

        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
