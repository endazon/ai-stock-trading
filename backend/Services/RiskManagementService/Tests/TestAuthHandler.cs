using System.Security.Claims;
using System.Text.Encodings.Web;
using RiskManagementService.Features.RiskManagement;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RiskManagementService.Tests;

// テスト用認証ハンドラ（platform AuthorizationService.Api.Tests 準拠）。JWT/Keycloak に依存せず
// ClaimsPrincipal を注入する。ロールはヘッダ "X-Test-Roles"（カンマ区切り）で指定する。
//   - ヘッダ無し          → 未認証（NoResult）→ OwnerOnly は 401
//   - "X-Test-Roles: a,b" → 指定ロールで認証（trading-owner を含めば OwnerOnly を通過、含まなければ 403）
//
// FR-20, FR-11, #868, IADR-0240 決定11, IADR-0383: 機密クライアント（client_credentials）のトークンを模すため、
// "X-Test-Azp"（`azp` クレーム）と "X-Test-Name"（名前クレーム。**NoName＝名前クレーム無し**＝稼働環境で
// 承認者が unknown になった形。空のヘッダ値は HttpClient が送らないため番兵値で表す）を任意で受ける。
// どちらも無ければ従来どおり名前は test-owner・azp 無し（ReportService.Tests の同名型と同じ形）。
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
            return Task.FromResult(AuthenticateResult.NoResult()); // 未認証 → 401

        var roles = header.ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var name = Request.Headers.TryGetValue(NameHeader, out var nameHeader) ? nameHeader.ToString() : "test-owner";

        var claims = new List<Claim>();
        if (name != NoName)
            claims.Add(new Claim(ClaimTypes.Name, name));
        if (Request.Headers.TryGetValue(AzpHeader, out var azp) && azp.ToString().Length > 0)
            claims.Add(new Claim(DelegatedActorResolver.AuthorizedPartyClaim, azp.ToString()));
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
