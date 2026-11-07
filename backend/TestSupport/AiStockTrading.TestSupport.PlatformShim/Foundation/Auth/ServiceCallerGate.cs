using System.Security.Claims;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;

// 🔴 NFR, NFR-05, NFR-06, IADR-0636: サービス（trading-service）のトークンを、**身元（azp）と宛先（aud）で**確かめ、
// **口ごとに呼んでよいサービスを限る。** 以前はサービスの分岐がロールだけを見て通していた（IADR-0611 の「サービスの分岐は従来どおり」）。
//
// 口（エンドポイント）は名前で指す: REST は `.RequireServiceCallers("<名前>")`、gRPC はサービスの型に `[ServiceCallers("<名前>")]`。
// 許す呼び手（`azp` の生の値・序数一致・ちょうど 1 つ）は既定 `ServiceCallerNames.DefaultCallers`、構成 `Auth:ServiceCallers:<名前>`（配列）が口ごとに置き換える。
//
// 段（構成 `Auth:ServiceToken:Mode`）:
//   - Off（既定。ローカル・CI）: 従前どおり（サービスのロールなら通す。aud を見ない）。
//   - Report: 合わない呼び手・宛先を Warning で残し、通す（一覧の確かめに使う）。
//   - Enforce: 宛先（aud が `Auth:ServiceToken:Audiences` のどれか）の合わないトークンを**認証で**拒む（401。所有者のトークンも同じ）。
//     名前の無い口・一覧に無い呼び手のサービスのトークンを**認可で**拒む（403）。
// 移行の段（構成 `Auth:ServiceToken:LegacyClients`。既定は空）: 一覧に在る `azp` のトークン（旧の共有クライアント）は、宛先を問わず、
//   サービスに開いたどの口も通す（Warning で残す＝全サービスが新しいクライアントで動くことをログで確かめてから外す）。
// 🔴 1 つの値（配列でない）で書かれた構成・Enforce で空の宛先は起動で止める（OwnerTokenBinding と同じ理由）。
public static class ServiceTokenBinding
{
    public const string ModeKey = "Auth:ServiceToken:Mode";
    public const string AudiencesKey = "Auth:ServiceToken:Audiences";
    public const string LegacyClientsKey = "Auth:ServiceToken:LegacyClients";
    public const string CallersKey = "Auth:ServiceCallers";

    /// <summary>既定の宛先。取引ユニットの API の audience（MSP の realm の ai-stock-trading-api-audience が載せる）。</summary>
    public static IReadOnlyList<string> DefaultAudiences { get; } = OwnerTokenBinding.DefaultAudiences;

    public static ServiceTokenSettings Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var modeText = configuration[ModeKey];
        ServiceTokenMode mode;
        if (string.IsNullOrWhiteSpace(modeText))
            mode = ServiceTokenMode.Off;
        else if (!Enum.TryParse(modeText.Trim(), ignoreCase: false, out mode) || !Enum.IsDefined(mode))
            throw new InvalidOperationException($"{ModeKey} は Off・Report・Enforce のどれかです（値: {modeText}。IADR-0636）。");

        var audiences = ReadArray(configuration, AudiencesKey) ?? DefaultAudiences;
        var legacy = ReadArray(configuration, LegacyClientsKey) ?? [];
        if (mode == ServiceTokenMode.Enforce && audiences.Count == 0)
            throw new InvalidOperationException($"{ModeKey}=Enforce には {AudiencesKey} に 1 つ以上の値が要ります（IADR-0636）。");

        // 既定（ServiceCallerNames.DefaultCallers）に、構成の口ごとの一覧を上書きする（口ごとに置き換える。足し合わせない）。
        var callers = new Dictionary<string, IReadOnlyList<string>>(ServiceCallerNames.DefaultCallers, StringComparer.Ordinal);
        var section = configuration.GetSection(CallersKey);
        if (!string.IsNullOrEmpty(section.Value))
            throw new InvalidOperationException($"{CallersKey} は口の名前ごとの配列で構成すること（IADR-0636）。");
        foreach (var child in section.GetChildren())
            callers[child.Key] = ReadArray(configuration, $"{CallersKey}:{child.Key}") ?? [];

        return new ServiceTokenSettings(mode, audiences, legacy, callers);
    }

    /// <summary>トークンの宛先（aud）が受けられるか（Enforce の認証で使う）。移行の段の旧クライアントは宛先を問わない。</summary>
    public static bool AudienceAccepted(IEnumerable<string>? audiences, string? authorizedParty, ServiceTokenSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (audiences?.Any(a => settings.Audiences.Contains(a, StringComparer.Ordinal)) == true)
            return true;
        return authorizedParty is not null && settings.LegacyClients.Contains(authorizedParty, StringComparer.Ordinal);
    }

    /// <summary>JwtBearer の宛先の確かめ（<see cref="AudienceAccepted"/> を JWT の azp と合わせる）。</summary>
    public static AudienceValidator AudienceValidatorFor(ServiceTokenSettings settings) =>
        (audiences, token, _) => AudienceAccepted(audiences, AuthorizedPartyOf(token), settings);

    private static string? AuthorizedPartyOf(SecurityToken? token) => token switch
    {
        JsonWebToken jwt when jwt.TryGetPayloadValue<string>(GrpcOwnerClientGate.AuthorizedPartyClaim, out var azp) => azp,
        _ => null,
    };

    /// <summary>ちょうど 1 つの azp の生の値（無い・空・2 つ以上なら null）。</summary>
    public static string? SingleAuthorizedParty(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var azps = user.FindAll(GrpcOwnerClientGate.AuthorizedPartyClaim).Select(c => c.Value).Take(2).ToList();
        return azps.Count == 1 && azps[0].Length > 0 ? azps[0] : null;
    }

    private static IReadOnlyList<string>? ReadArray(IConfiguration configuration, string key)
    {
        // 空の値は未構成と同じ（試験の WebApplicationFactory は配列の親の鍵を空の値で渡す）。値のある 1 つの値だけを誤りとする。
        if (!string.IsNullOrEmpty(configuration.GetSection(key).Value))
            throw new InvalidOperationException(
                $"{key} は配列で構成すること（環境変数なら {key.Replace(":", "__", StringComparison.Ordinal)}__0=...。IADR-0636）。");
        var children = configuration.GetSection(key).GetChildren().ToList();
        if (children.Count == 0)
            return null;
        return children
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}

/// <summary>NFR, IADR-0636: サービスのトークンの確かめの段。</summary>
public enum ServiceTokenMode
{
    /// <summary>既定: 従前どおり（ロールだけ）。</summary>
    Off = 0,

    /// <summary>合わないものを記録し、通す。</summary>
    Report = 1,

    /// <summary>宛先の合わないトークンは 401、一覧に無い呼び手は 403。</summary>
    Enforce = 2,
}

/// <summary>NFR, IADR-0636: 解決済みの設定。</summary>
public sealed record ServiceTokenSettings(
    ServiceTokenMode Mode,
    IReadOnlyList<string> Audiences,
    IReadOnlyList<string> LegacyClients,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Callers);

/// <summary>NFR, IADR-0636: 口の名前（サービスに開く口の許可の一覧を引く鍵）。gRPC はサービスの型に付ける。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ServiceCallersAttribute : Attribute
{
    public ServiceCallersAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }
}

/// <summary>NFR, IADR-0636: サービスのトークンの身元と口の一覧を確かめる要件（OwnerOrService・GrpcOwnerOrService・サービスだけの口）。</summary>
public sealed record ServiceCallerRequirement : IAuthorizationRequirement;

public static class ServiceCallersEndpointExtensions
{
    /// <summary>
    /// NFR, IADR-0636: 口に名前を付け、サービスのトークンの呼び手を一覧（<c>Auth:ServiceCallers:&lt;名前&gt;</c>）で限る。
    /// サービスだけの口（独自のポリシー）にも使えるよう、要件も足す（OwnerOrService の口では要件が重なるだけ）。
    /// </summary>
    public static TBuilder RequireServiceCallers<TBuilder>(this TBuilder builder, string name)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new ServiceCallersAttribute(name));
        builder.RequireAuthorization(policy => policy.RequireAuthenticatedUser().AddRequirements(new ServiceCallerRequirement()));
        return builder;
    }
}

/// <summary>NFR, IADR-0636: <see cref="ServiceCallerRequirement"/> の判定器。</summary>
public sealed class ServiceCallerHandler(ServiceTokenSettings settings, ILogger<ServiceCallerHandler> logger)
    : AuthorizationHandler<ServiceCallerRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ServiceCallerRequirement requirement)
    {
        var user = context.User;
        if (user?.Identity?.IsAuthenticated != true)
            return Task.CompletedTask;

        // サービスのトークンでなければ、ここは関わらない（所有者の判定はほかの要件が持つ）。
        if (!user.IsInRole(AiStockTradingAuthPolicies.ServiceRole) || settings.Mode == ServiceTokenMode.Off)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var azp = ServiceTokenBinding.SingleAuthorizedParty(user);
        var name = NameOf(context.Resource);
        if (azp is not null && settings.LegacyClients.Contains(azp, StringComparer.Ordinal))
        {
            logger.LogWarning(
                "移行の段の共有クライアントのトークンを通します（全サービスが自分のクライアントへ移ったら Auth:ServiceToken:LegacyClients を外す）: azp={Azp} 口={Name}",
                Shorten(azp), Shorten(name ?? "-"));
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var callerOk = azp is not null && name is not null
            && settings.Callers.TryGetValue(name, out var allowed) && allowed.Contains(azp, StringComparer.Ordinal);
        // 宛先は Enforce なら認証（JwtBearer）が確かめ済み。Report では記録のために見る。
        var audienceOk = settings.Mode == ServiceTokenMode.Enforce
            || ServiceTokenBinding.AudienceAccepted(user.FindAll(OwnerTokenBinding.AudienceClaim).Select(c => c.Value), null, settings);
        if (callerOk && audienceOk)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var aud = Shorten(string.Join(",", user.FindAll(OwnerTokenBinding.AudienceClaim).Select(c => c.Value)));
        if (settings.Mode == ServiceTokenMode.Enforce)
        {
            logger.LogWarning(
                "この口を呼んでよいサービスでないため拒否します: azp={Azp} 口={Name} aud={Aud}",
                Shorten(azp ?? "-"), Shorten(name ?? "（名前なし）"), aud);
            return Task.CompletedTask;
        }

        logger.LogWarning(
            "この口を呼んでよいサービスか宛先が一覧に無いが、段 Report のため通します: azp={Azp} 口={Name} aud={Aud}",
            Shorten(azp ?? "-"), Shorten(name ?? "（名前なし）"), aud);
        context.Succeed(requirement);
        return Task.CompletedTask;
    }

    private static string? NameOf(object? resource) =>
        resource is HttpContext http ? http.GetEndpoint()?.Metadata.GetMetadata<ServiceCallersAttribute>()?.Name : null;

    private static string Shorten(string value) => OwnerTokenBindingHandler.Shorten(value);
}
