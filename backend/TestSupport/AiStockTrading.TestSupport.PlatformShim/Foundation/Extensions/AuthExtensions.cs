using System.Security.Claims;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;

// IADR-0011（platform ADR-0004 の最小移植・由来: Foundation/Extensions/AuthExtensions.cs）:
// 認可ポリシー／ロールの名称定数。kill switch 操作・リスク設定変更など「利用者のみ」の操作に用いる。
public static class AiStockTradingAuthPolicies
{
    // FR-10/FR-19/FR-20, ADR-0003/ADR-0007/ADR-0008: kill switch・リスク設定・段階昇格は利用者のみ操作できる。
    public const string OwnerOnly = "OwnerOnly";

    // IADR-0051: 読み取り系の同期照会（sizing-context / open-positions / daily-policy）は利用者またはサービスが呼べる。
    // 書き込み系は OwnerOnly 据え置き（サービスへ書き込み権限を与えない＝最小権限）。
    public const string OwnerOrService = "OwnerOrService";

    // NFR-06, FR-14, ADR-0047 決定 3, IADR-0448, #1067: **east-west gRPC 面の門**。サービス（trading-service）は
    // OwnerOrService と同じ。所有者（trading-owner）は、トークンの `azp` が Discord ボットの機密クライアント
    // （構成 `Auth:GrpcOwnerClients`。既定 ai-stock-trading-owner）であるときだけ通す＝人の利用者のトークンは gRPC 面を通らない。
    // 🔴 REST の面には付けない（REST の所有者の判定は OwnerOrService のまま。判定は GrpcOwnerClientGate）。
    public const string GrpcOwnerOrService = "GrpcOwnerOrService";

    // NFR-06, FR-14, ADR-0047 決定 1〜3, IADR-0449 決定 2, #753（段 5）: **所有者限定（REST の OwnerOnly）の gRPC 面の門**。
    // trading-owner ∧ トークンの `azp` が Discord ボットの機密クライアント（GrpcOwnerOrService の所有者の分岐と同じ許可集合）。
    // REST の OwnerOnly と同じく s2s（trading-service）には開かない。🔴 REST の面には付けない。
    public const string GrpcOwnerOnly = "GrpcOwnerOnly";

    // 利用者ロール（Keycloak のレルムロール想定）。単独利用者運用のため単層とする（IADR-0011）。
    public const string OwnerRole = "trading-owner";

    // IADR-0051: サービス間 s2s 認証用の最小権限ロール（読み取り系のみ許可）。kill switch/設定変更は持たない。
    public const string ServiceRole = "trading-service";
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

        // NFR-06, ADR-0047 決定 3, IADR-0448 決定 2, #1067: gRPC 面の所有者の門の許可集合を**起動時に**解決する。
        // 1 つの値の構成はここで例外（既定へ静かに戻さない）。全サービスが本拡張を通るので、gRPC 面を持たないサービスでも
        // 誤った構成は起動で止まる（構成の書き手に気付かせる向き。値そのものは gRPC 面を持つサービスでだけ効く）。
        var grpcOwnerClients = GrpcOwnerClientGate.EffectiveClients(config);

        // FR-10/FR-19/FR-20, ADR-0003/ADR-0007/ADR-0008: 利用者のみのエンドポイント用に OwnerOnly ポリシーを登録する。
        services.AddAuthorization(options =>
        {
            options.AddPolicy(AiStockTradingAuthPolicies.OwnerOnly, policy =>
                policy.RequireRole(AiStockTradingAuthPolicies.OwnerRole));

            // IADR-0051: 読み取り系の同期照会は利用者（trading-owner）またはサービス（trading-service）が呼べる。
            options.AddPolicy(AiStockTradingAuthPolicies.OwnerOrService, policy =>
                policy.RequireRole(AiStockTradingAuthPolicies.OwnerRole, AiStockTradingAuthPolicies.ServiceRole));

            // NFR-06, FR-14, ADR-0047 決定 3, IADR-0448 決定 1, #1067: gRPC 面の門。s2s の分岐は OwnerOrService と同じ、
            // 所有者の分岐は azp がボットの機密クライアントであることを併せて求める（GrpcOwnerClientGate.Allows）。
            options.AddPolicy(AiStockTradingAuthPolicies.GrpcOwnerOrService, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => GrpcOwnerClientGate.Allows(context.User, grpcOwnerClients)));

            // NFR-06, FR-14, ADR-0047 決定 3, IADR-0449 決定 2, #753（段 5）: 所有者限定の gRPC 面の門。所有者の分岐だけ（s2s は通さない）。
            options.AddPolicy(AiStockTradingAuthPolicies.GrpcOwnerOnly, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => GrpcOwnerClientGate.AllowsOwner(context.User, grpcOwnerClients)));
        });
        return services;
    }
}
