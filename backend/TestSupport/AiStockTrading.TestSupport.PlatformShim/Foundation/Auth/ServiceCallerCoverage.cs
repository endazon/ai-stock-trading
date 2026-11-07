using System.Security.Claims;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;

/// <summary>
/// NFR, IADR-0637: 本物の組み立ての経路表（<see cref="EndpointDataSource"/>）から、**サービスのロール（trading-service）で通り得るのに口の名前
/// （<see cref="ServiceCallersAttribute"/>。REST は <c>.RequireServiceCallers</c>、gRPC は型の <c>[ServiceCallers]</c>）を持たない経路**を挙げる。
/// 段 Enforce では、そうした経路はサービスから 403 になる（閉じる向き）。口を足したときの名前の付け忘れを、各サービスの試験が止める。
/// 「通り得る」は、経路の認可（<see cref="IAuthorizeData"/> と経路に付けた <see cref="AuthorizationPolicy"/> を合わせた方針）を、
/// **サービスのロールだけを持つ利用者で実際に評価して通る**ことである（段 Off。<c>RequireAssertion</c> のような中身の見えない要件も評価で拾う）。
/// 評価で通らなくても、方針が trading-service を許すロール・サービスを許す結び付き・<see cref="ServiceCallerRequirement"/> を持てば数える（判定の取りこぼしを避ける）。
/// </summary>
public static class ServiceCallerCoverage
{
    /// <summary>名前の無い経路（"METHOD テンプレート"。gRPC は "POST /パッケージ.サービス/メソッド"）。</summary>
    public static async Task<IReadOnlyList<string>> UnnamedServiceRoutesAsync(IServiceProvider services)
    {
        var provider = services.GetRequiredService<IAuthorizationPolicyProvider>();
        var unnamed = new List<string>();
        var checkedCount = 0;
        foreach (var endpoint in services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var policy = await AuthorizationPolicy.CombineAsync(
                provider,
                endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());
            if (policy is null || !(AdmitsService(policy) || await AuthorizesServiceAsync(services, endpoint, policy)))
                continue;

            checkedCount++;
            if (endpoint.Metadata.GetMetadata<ServiceCallersAttribute>() is null)
                unnamed.Add(Describe(endpoint));
        }

        if (checkedCount == 0)
            throw new InvalidOperationException("サービスのロールで通り得る経路が 1 つも無い（経路表が組めていないか、判定が壊れている。0 件の照合は何も証明しない）");
        return unnamed.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>判定そのもの（試験から陰性対照に使う）。</summary>
    public static bool AdmitsService(AuthorizationPolicy policy) => policy.Requirements.Any(r => r switch
    {
        RolesAuthorizationRequirement roles => roles.AllowedRoles.Contains(AiStockTradingAuthPolicies.ServiceRole, StringComparer.Ordinal),
        OwnerTokenBindingRequirement binding => binding.AllowService,
        ServiceCallerRequirement => true,
        _ => false,
    });

    private static async Task<bool> AuthorizesServiceAsync(IServiceProvider services, Endpoint endpoint, AuthorizationPolicy policy)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, AiStockTradingAuthPolicies.ServiceRole), new Claim("azp", ServiceCallerNames.ClientPrefix + "coverage-probe")],
            "service-caller-coverage", ClaimTypes.Name, ClaimTypes.Role);
        var http = new DefaultHttpContext { RequestServices = services };
        http.SetEndpoint(endpoint);
        var result = await services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(new ClaimsPrincipal(identity), http, policy);
        return result.Succeeded;
    }

    private static string Describe(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
        return $"{string.Join(",", methods)} /{(endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/')}";
    }
}
