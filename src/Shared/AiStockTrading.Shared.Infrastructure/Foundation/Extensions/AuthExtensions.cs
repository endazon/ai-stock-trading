using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiStockTrading.Shared.Infrastructure.Foundation.Extensions;

// IADR-0011（platform ADR-0004 の最小移植・由来: Foundation/Extensions/AuthExtensions.cs）:
// 認可ポリシー／ロールの名称定数。kill switch 操作・リスク設定変更など「利用者のみ」の操作に用いる。
public static class AiStockTradingAuthPolicies
{
    // FR-10/FR-19/FR-20, ADR-0007: kill switch・リスク設定・段階昇格は利用者のみ操作できる。
    public const string OwnerOnly = "OwnerOnly";

    // 利用者ロール（Keycloak のレルムロール想定）。単独利用者運用のため単層とする（IADR-0011）。
    public const string OwnerRole = "trading-owner";
}

public static class AuthExtensions
{
    // ADR-0004（platform）: Keycloak OIDC/JWT 認証。利用者のみの操作を RBAC で守る。
    public static IServiceCollection AddAiStockTradingAuth(
        this IServiceCollection services,
        IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var authority = config["Auth:Authority"]
            ?? "http://keycloak:8080/realms/ai-stock-trading";

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters.ValidateAudience = false;
                // RequireRole/IsInRole が参照するロールクレーム型を明示する。実 Keycloak のレルムロールは
                // realm_access.roles に格納され、標準ハンドラでは ClaimTypes.Role へ展開されないため、
                // 下記の IClaimsTransformation で補う。
                options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
                // Identity.Name が参照する名前クレームを Keycloak の preferred_username に合わせる
                // （既定マップは unique_name のみ写像するため、実トークンでは Name が null になり、
                // 監査ログの subject が anonymous へ潰れる）。
                options.TokenValidationParameters.NameClaimType = "preferred_username";
            });

        // ADR-0004（platform）: Keycloak の realm_access.roles を ClaimTypes.Role へ展開する。
        // これがないと RequireRole("trading-owner") が実トークンにマッチしない。
        services.AddTransient<IClaimsTransformation, KeycloakRolesClaimsTransformation>();

        // FR-10/FR-19/FR-20, ADR-0007: 利用者のみのエンドポイント用に OwnerOnly ポリシーを登録する。
        services.AddAuthorization(options =>
        {
            options.AddPolicy(AiStockTradingAuthPolicies.OwnerOnly, policy =>
                policy.RequireRole(AiStockTradingAuthPolicies.OwnerRole));
        });
        return services;
    }
}
