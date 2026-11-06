using System.Security.Claims;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

    // 🔴 NFR-06, FR-14, #1554, IADR-0622: Discord ボット専用のロール。ボットのクライアント（azp）かつボットの許可表の経路
    // （REST の AllowOwnerBot）と gRPC の所有者の面でだけ、所有者と同じに扱う。他では通さない（OwnerTokenBindingHandler）。
    public const string BotRole = "trading-bot";

    // IADR-0051: サービス間 s2s 認証用の最小権限ロール（読み取り系のみ許可）。kill switch/設定変更は持たない。
    public const string ServiceRole = "trading-service";
}

public static class AuthExtensions
{
    // ADR-0004（platform）: Keycloak OIDC/JWT 認証。利用者のみの操作を RBAC で守る。
    // 🔴 NFR, #1529, IADR-0612: environment を渡すと、Production では単体実行向けの既定（コードの既定の Authority・単体実行向けのレルム）を
    // 使わせない（起動で止める。StandaloneRealmGuard）。Program.cs は builder.Environment を必ず渡す（StandaloneRealmGuardWiringTests が固定）。
    public static IServiceCollection AddAiStockTradingAuth(
        this IServiceCollection services,
        IConfiguration config,
        IHostEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (environment is not null)
            StandaloneRealmGuard.Validate(config, environment);

        var authority = config["Auth:Authority"]
            ?? StandaloneRealmGuard.DevelopmentDefaultAuthority;

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

        // 🔴 NFR, FR-10, #1528, IADR-0611: REST の所有者の判定で aud / azp も確かめる（段 1 は記録だけ）。設定は起動時に解決し、誤りはここで止める。
        var ownerTokenBinding = OwnerTokenBinding.Resolve(config);
        services.AddSingleton(ownerTokenBinding);
        // 🔴 NFR-06, #1554, IADR-0622: ボットの許可表の段（Auth:OwnerBot:Mode。aud / azp の段とは別）。
        services.AddSingleton(OwnerBotScope.Resolve(config));
        services.AddSingleton<IAuthorizationHandler, OwnerTokenBindingHandler>();

        // FR-10/FR-19/FR-20, ADR-0003/ADR-0007/ADR-0008: 利用者のみのエンドポイント用に OwnerOnly ポリシーを登録する。
        services.AddAuthorization(options =>
        {
            // #1554, IADR-0622: ボット専用のロールも受けるが、通すかは判定器が決める（ボットのクライアントかつ許可表の経路だけ）。
            options.AddPolicy(AiStockTradingAuthPolicies.OwnerOnly, policy => policy
                .RequireRole(AiStockTradingAuthPolicies.OwnerRole, AiStockTradingAuthPolicies.BotRole)
                .AddRequirements(new OwnerTokenBindingRequirement(AllowService: false)));

            // IADR-0051: 読み取り系の同期照会は利用者（trading-owner）またはサービス（trading-service）が呼べる。
            // #1528, IADR-0611: 所有者の分岐は aud / azp も確かめる（サービスの分岐は従来どおり）。
            options.AddPolicy(AiStockTradingAuthPolicies.OwnerOrService, policy => policy
                .RequireRole(AiStockTradingAuthPolicies.OwnerRole, AiStockTradingAuthPolicies.ServiceRole, AiStockTradingAuthPolicies.BotRole)
                .AddRequirements(new OwnerTokenBindingRequirement(AllowService: true)));

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

/// <summary>
/// 🔴 NFR, #1529, IADR-0612: 単体実行（docker-compose・Development）向けの認証の既定が Production の構成へ漏れないようにする。
/// <list type="bullet">
/// <item>Production で <c>Auth:Authority</c> が無い・空なら起動で止める（コードの既定へ黙って倒さない）。</item>
/// <item>Production で Authority が単体実行向けのレルム（<c>/realms/ai-stock-trading</c>。<c>infra/keycloak/realm-export.json</c> の
/// 開発用クライアントを持つ）を指すなら、明示の opt-in（<c>Auth:AllowStandaloneRealm=true</c>）が無い限り起動で止める。</item>
/// </list>
/// Production 以外（Development・試験の環境名）は従前どおり。
/// </summary>
public static class StandaloneRealmGuard
{
    /// <summary>Production 以外で Authority が無いときの既定（単体実行向け）。</summary>
    public const string DevelopmentDefaultAuthority = "http://keycloak:8080/realms/ai-stock-trading";

    /// <summary>単体実行向けのレルムの名前。</summary>
    public const string StandaloneRealm = "ai-stock-trading";

    /// <summary>明示の opt-in の構成キー。</summary>
    public const string AllowKey = "Auth:AllowStandaloneRealm";

    public static void Validate(IConfiguration config, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(environment);
        if (!environment.IsProduction())
            return;

        var authority = config["Auth:Authority"];
        if (string.IsNullOrWhiteSpace(authority))
            throw new InvalidOperationException(
                "Production では Auth:Authority を明示すること（コードの既定〔単体実行向けのレルム〕へは倒さない）。");

        if (IsStandaloneRealm(authority) && !string.Equals(config[AllowKey], "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Production の Auth:Authority が単体実行向けのレルム（{StandaloneRealm}）を指しています。"
                + $"配備のレルム（例 global.authAuthority）を指すか、意図して使うときだけ {AllowKey}=true を明示すること。");
    }

    /// <summary>Authority のパスが <c>/realms/ai-stock-trading</c> で終わるか（末尾の / は無視。大小文字は区別しない）。</summary>
    public static bool IsStandaloneRealm(string authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var path = Uri.TryCreate(authority.Trim(), UriKind.Absolute, out var uri) ? uri.AbsolutePath : authority.Trim();
        return path.TrimEnd('/').EndsWith("/realms/" + StandaloneRealm, StringComparison.OrdinalIgnoreCase);
    }
}
