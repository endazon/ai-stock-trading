using System.Security.Claims;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AiStockTrading.Shared.Contracts.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;

// 🔴 NFR, FR-10, #1528, IADR-0611: REST の所有者の判定（OwnerOnly・OwnerOrService の所有者の分岐）で、ロールに加えて
// トークンの宛先（aud）と発行先のクライアント（azp）を確かめる。
//
// 判定（所有者の分岐だけ。サービスの分岐〔trading-service〕は従来どおり aud / azp を見ない）:
//   - aud: トークンの `aud` クレーム（複数あり得る）のどれかが許可集合（構成 Auth:OwnerToken:Audiences）に序数一致で在る。
//   - azp: `azp` クレームがちょうど 1 つ・生の値が許可集合（構成 Auth:OwnerToken:Clients）に序数一致で在る（gRPC 面の門と同じ判定）。
// 段（構成 Auth:OwnerToken:Mode）:
//   - Report（段 1・既定）: 合わないトークンを Warning で残し、通す（従前どおり）。
//   - Enforce（段 2）: 合わないトークンを通さない（403）。
// 許可集合の既定は、MSP レルムで所有者の操作を中継する BFF（`bff`）と Discord ボットの機密クライアント（`ai-stock-trading-owner`）、
// 宛先は AST 専用の `ai-stock-trading-api`（Keycloak の audience mapper で、所有者の操作を送る 2 つのクライアントにだけ付ける）。
// 🔴 MSP 共通の宛先（`platform-api`）は使わない —— MSP の全サービスアカウントのトークンに付くため、宛先の確かめにならない。
// 🔴 1 つの値（配列でない）で書かれていたら起動時に例外（GrpcOwnerClientGate.ThrowIfScalar と同じ理由）。段 2 で空の集合も起動時に例外。
public static class OwnerTokenBinding
{
    public const string ModeKey = "Auth:OwnerToken:Mode";
    public const string AudiencesKey = "Auth:OwnerToken:Audiences";
    public const string ClientsKey = "Auth:OwnerToken:Clients";
    public const string AudienceClaim = "aud";

    public static IReadOnlyList<string> DefaultAudiences { get; } = ["ai-stock-trading-api"];

    public static IReadOnlyList<string> DefaultClients { get; } = ["bff", GrpcOwnerClientGate.DefaultClients[0]];

    /// <summary>構成から判定の設定を解決する（起動時に 1 回）。</summary>
    public static OwnerTokenBindingSettings Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var modeText = configuration[ModeKey];
        OwnerTokenBindingMode mode;
        if (string.IsNullOrWhiteSpace(modeText))
            mode = OwnerTokenBindingMode.Report;
        else if (!Enum.TryParse(modeText.Trim(), ignoreCase: false, out mode) || !Enum.IsDefined(mode))
            throw new InvalidOperationException($"{ModeKey} は Report か Enforce です（値: {modeText}）。");

        var audiences = ReadArray(configuration, AudiencesKey, DefaultAudiences);
        var clients = ReadArray(configuration, ClientsKey, DefaultClients);
        if (mode == OwnerTokenBindingMode.Enforce && (audiences.Count == 0 || clients.Count == 0))
            throw new InvalidOperationException(
                $"{ModeKey}=Enforce には {AudiencesKey} と {ClientsKey} の両方に 1 つ以上の値が要ります（空では所有者の操作が全部止まります）。");

        return new OwnerTokenBindingSettings(mode, audiences, clients);
    }

    /// <summary>aud と azp が許可集合に在るか。</summary>
    public static bool Matches(ClaimsPrincipal user, OwnerTokenBindingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(settings);

        var audienceOk = user.FindAll(AudienceClaim)
            .Any(c => settings.Audiences.Contains(c.Value, StringComparer.Ordinal));
        return audienceOk && GrpcOwnerClientGate.IsTrustedOwnerClient(user, settings.Clients);
    }

    private static IReadOnlyList<string> ReadArray(IConfiguration configuration, string key, IReadOnlyList<string> fallback)
    {
        if (configuration.GetSection(key).Value is not null)
            throw new InvalidOperationException(
                $"{key} は配列で構成すること（環境変数なら {key.Replace(":", "__", StringComparison.Ordinal)}__0=...）。");

        // 子要素が 1 つも無い＝未構成 → 既定。子要素があれば既定を置き換える（足し合わせない）。
        // 子要素が全部空白だけのときは空の集合になる: 段 1 は全トークンが不一致として記録されて通り、段 2 は Resolve が起動で止める
        // （空の許可集合で所有者の操作が全部 403 になる構成を黙って動かさない）。
        var children = configuration.GetSection(key).GetChildren().ToList();
        if (children.Count == 0)
            return fallback;

        return children
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}

/// <summary>NFR, #1528, IADR-0611: 所有者のトークンの宛先・クライアントの確かめの段。</summary>
public enum OwnerTokenBindingMode
{
    /// <summary>段 1（既定）: 合わないトークンを記録し、通す。</summary>
    Report = 0,

    /// <summary>段 2: 合わないトークンを通さない。</summary>
    Enforce = 1,
}

/// <summary>NFR, #1528, IADR-0611: 解決済みの設定。</summary>
public sealed record OwnerTokenBindingSettings(
    OwnerTokenBindingMode Mode, IReadOnlyList<string> Audiences, IReadOnlyList<string> Clients);

/// <summary>
/// NFR, #1528, IADR-0611: REST の所有者の判定の要件。<paramref name="AllowService"/> は OwnerOrService（サービスの分岐を従来どおり通す）。
/// </summary>
public sealed record OwnerTokenBindingRequirement(bool AllowService) : IAuthorizationRequirement;

/// <summary>NFR, #1528, IADR-0611: <see cref="OwnerTokenBindingRequirement"/> の判定器。</summary>
public sealed class OwnerTokenBindingHandler(
    OwnerTokenBindingSettings settings, OwnerBotScopeSettings botScope, ILogger<OwnerTokenBindingHandler> logger)
    : AuthorizationHandler<OwnerTokenBindingRequirement>
{
    internal const int MaxLoggedChars = 64;

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, OwnerTokenBindingRequirement requirement)
    {
        var user = context.User;
        if (user?.Identity?.IsAuthenticated != true)
            return Task.CompletedTask;

        // サービスの分岐は従来どおり（aud / azp を見ない）。
        if (requirement.AllowService && user.IsInRole(AiStockTradingAuthPolicies.ServiceRole))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // 所有者（ボット専用のロールを含む）でなければ何もしない（ロールの要件が落とす）。
        var isOwner = user.IsInRole(AiStockTradingAuthPolicies.OwnerRole);
        var isBotRole = user.IsInRole(AiStockTradingAuthPolicies.BotRole);
        if (!isOwner && !isBotRole)
            return Task.CompletedTask;

        // 🔴 NFR-06, #1554, IADR-0622: ボットの資格情報は、ボットが使う経路（許可表の印）にだけ届く。
        if (!AllowsBotScope(context, user, isBotRole))
            return Task.CompletedTask;

        if (OwnerTokenBinding.Matches(user, settings))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var azp = Shorten(string.Join(",", user.FindAll(GrpcOwnerClientGate.AuthorizedPartyClaim).Select(c => c.Value)));
        var aud = Shorten(string.Join(",", user.FindAll(OwnerTokenBinding.AudienceClaim).Select(c => c.Value)));
        if (settings.Mode == OwnerTokenBindingMode.Enforce)
        {
            logger.LogWarning(
                "所有者のトークンの宛先またはクライアントが許可されていないため拒否します（段 2）: azp={Azp} aud={Aud}", azp, aud);
            return Task.CompletedTask;
        }

        logger.LogWarning(
            "所有者のトークンの宛先またはクライアントが許可集合に無いが、段 1 のため通します: azp={Azp} aud={Aud}", azp, aud);
        context.Succeed(requirement);
        return Task.CompletedTask;
    }

    // NFR-06, #1554, IADR-0622: ボットの許可表の判定。false なら通さない（呼び出し側は Succeed しない）。
    //   - trading-bot: ボットのクライアントかつ印の付いた経路でだけ通す（段に関係なく。誰も持っていなかったロールの追加）。
    //   - ボットのクライアントの trading-owner: 印の無い経路は、段 1 は記録して通し、段 2 は通さない。
    //   - それ以外（利用者のトークン）: 見ない。
    private bool AllowsBotScope(AuthorizationHandlerContext context, ClaimsPrincipal user, bool isBotRole)
    {
        var fromBot = OwnerBotScope.IsBotClient(user, botScope);
        var allowedRoute = OwnerBotScope.IsAllowedForBot(context.Resource);
        var azp = Shorten(string.Join(",", user.FindAll(GrpcOwnerClientGate.AuthorizedPartyClaim).Select(c => c.Value)));

        if (isBotRole && !(fromBot && allowedRoute))
        {
            logger.LogWarning(
                "ボット専用のロールのトークンを、ボットのクライアントかつボットの許可表の経路でないため拒否します: azp={Azp} route={Route}",
                azp, RouteOf(context.Resource));
            return false;
        }

        if (!fromBot || allowedRoute)
            return true;

        if (botScope.Mode == OwnerTokenBindingMode.Enforce)
        {
            logger.LogWarning(
                "ボットのクライアントのトークンがボットの許可表の外の所有者の経路を呼んだため拒否します（段 2）: azp={Azp} route={Route}",
                azp, RouteOf(context.Resource));
            return false;
        }

        logger.LogWarning(
            "ボットのクライアントのトークンがボットの許可表の外の所有者の経路を呼んだが、段 1 のため通します: azp={Azp} route={Route}",
            azp, RouteOf(context.Resource));
        return true;
    }

    // 記録に載せる経路（メソッドと雛形。値は載せない）。
    private static string RouteOf(object? resource)
    {
        if (resource is not HttpContext http)
            return "-";
        var pattern = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "-";
        return Shorten($"{http.Request.Method} {pattern}");
    }

    // ログへ出す前に制御文字を落として切り詰め、行を割る文字を共通の無害化（LogSanitizer）に通す（トークンのクレームと経路は外から来る値。
    // CWE-117。#1541 の aud / azp の記録と #1554 のボットの許可表の記録の両方がここを通る）。
    internal static string Shorten(string value)
    {
        var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray());
        var cut = cleaned.Length <= MaxLoggedChars ? cleaned : cleaned[..MaxLoggedChars] + "…";
        return LogSanitizer.Sanitize(cut, MaxLoggedChars + 1) ?? string.Empty;
    }
}
