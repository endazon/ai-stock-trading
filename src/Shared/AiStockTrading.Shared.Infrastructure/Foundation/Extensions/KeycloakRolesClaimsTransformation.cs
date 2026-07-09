using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace AiStockTrading.Shared.Infrastructure.Foundation.Extensions;

// IADR-0011（platform ADR-0004 の最小移植・由来: Foundation/Extensions/KeycloakRolesClaimsTransformation.cs）:
// Keycloak のレルムロール（realm_access.roles）を ClaimTypes.Role へ展開する。
// 標準の JwtBearerHandler は realm_access をネストした JSON クレームとして保持するだけで、個々のロールを
// role クレームへ自動展開しない。そのため RequireRole("trading-owner") が実 Keycloak トークンにマッチせず、
// 正規の利用者でも OwnerOnly を通過できない。本 IClaimsTransformation でトークン検証後に展開する。
public sealed class KeycloakRolesClaimsTransformation : IClaimsTransformation
{
    // Keycloak がレルムロールを格納する JSON クレーム名（{"roles":[...]}）。
    private const string RealmAccessClaim = "realm_access";

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        // 未認証・非 ClaimsIdentity は素通し（fail-closed: ロールを付与しない）。
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
            return Task.FromResult(principal);

        var realmAccess = principal.FindFirst(RealmAccessClaim)?.Value;
        if (string.IsNullOrWhiteSpace(realmAccess))
            return Task.FromResult(principal);

        // 複数回呼ばれ得る（AuthenticateAsync 毎）ため、重複を避けつつ冪等に展開する。
        foreach (var role in ExtractRoles(realmAccess))
        {
            if (!identity.HasClaim(ClaimTypes.Role, role))
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        return Task.FromResult(principal);
    }

    // realm_access クレーム（例: {"roles":["trading-owner","user"]}）から roles を取り出す。
    // 不正な JSON・想定外の形状は無視する（fail-closed: ロールを付与しない）。
    private static List<string> ExtractRoles(string realmAccessJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(realmAccessJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("roles", out var roles)
                || roles.ValueKind != JsonValueKind.Array)
                return [];

            return roles.EnumerateArray()
                .Where(r => r.ValueKind == JsonValueKind.String)
                .Select(r => r.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
