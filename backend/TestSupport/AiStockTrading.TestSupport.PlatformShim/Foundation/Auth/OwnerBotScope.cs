using System.Security.Claims;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;

// 🔴 NFR-06, FR-14, UC-06, ADR-0047, #1554, IADR-0622（IADR-0615 決定 7 の B）: ボットの資格情報で届く REST の所有者の操作を、
// **ボットが使う経路の許可表**と**ボット専用のロール**で絞る。
//
// - ボットのクライアント（トークンの azp が Auth:GrpcOwnerClients の集合。既定 ai-stock-trading-owner）の所有者のトークンが、
//   印（AllowOwnerBot）の無い所有者限定の経路へ来たら、段（Auth:OwnerBot:Mode）が Report（段 1・既定）なら記録して通し、
//   Enforce（段 2）なら通さない。
// - ロール trading-bot は、ボットのクライアントかつ印の付いた経路でだけ所有者と同じに扱う（段に関係なく、それ以外では通さない。
//   誰も持っていないロールの追加なので、配備中の通信を止めない）。
// 判定は OwnerTokenBindingHandler（OwnerOnly・OwnerOrService の所有者の分岐）が行う。#1541 の aud / azp の判定（Auth:OwnerToken:Mode）とは
// 別の段で、片方ずつ段 2 へ上げられる。
public static class OwnerBotScope
{
    public const string ModeKey = "Auth:OwnerBot:Mode";

    /// <summary>構成から段とボットのクライアントの集合を解決する（起動時に 1 回）。</summary>
    public static OwnerBotScopeSettings Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var modeText = configuration[ModeKey];
        OwnerTokenBindingMode mode;
        if (string.IsNullOrWhiteSpace(modeText))
            mode = OwnerTokenBindingMode.Report;
        else if (!Enum.TryParse(modeText.Trim(), ignoreCase: false, out mode) || !Enum.IsDefined(mode))
            throw new InvalidOperationException($"{ModeKey} は Report か Enforce です（値: {modeText}）。");

        return new OwnerBotScopeSettings(mode, GrpcOwnerClientGate.EffectiveClients(configuration));
    }

    /// <summary>トークンがボットのクライアントのものか（azp がちょうど 1 つ・集合に序数一致）。</summary>
    public static bool IsBotClient(ClaimsPrincipal user, OwnerBotScopeSettings settings) =>
        GrpcOwnerClientGate.IsTrustedOwnerClient(user, settings.BotClients);

    /// <summary>認可の対象（エンドポイントの経路の HttpContext か Endpoint）に、ボットの許可表の印があるか。</summary>
    public static bool IsAllowedForBot(object? resource)
    {
        var endpoint = resource switch
        {
            HttpContext http => http.GetEndpoint(),
            Endpoint e => e,
            _ => null,
        };
        return endpoint?.Metadata.GetMetadata<OwnerBotOperation>() is not null;
    }
}

/// <summary>NFR-06, #1554, IADR-0622: 解決済みの設定。BotClients は gRPC の所有者の面の門と同じ集合。</summary>
public sealed record OwnerBotScopeSettings(OwnerTokenBindingMode Mode, IReadOnlyList<string> BotClients);

/// <summary>NFR-06, #1554, IADR-0622: ボットの許可表の印（エンドポイントのメタデータ）。</summary>
public sealed class OwnerBotOperation
{
    public static OwnerBotOperation Instance { get; } = new();

    private OwnerBotOperation()
    {
    }
}

/// <summary>NFR-06, #1554, IADR-0622: 経路にボットの許可表の印を付ける。</summary>
public static class OwnerBotEndpointExtensions
{
    /// <summary>
    /// この経路をボットの許可表に載せる。🔴 付けてよいのは <see cref="OwnerBotRoutes.All"/> に在る経路だけ
    /// （各提供側の試験が「印の付いた経路＝許可表」を集合で確かめ、許可表はボットの REST アダプタの経路と突き合わせる）。
    /// </summary>
    public static TBuilder AllowOwnerBot<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(OwnerBotOperation.Instance);
}

/// <summary>
/// NFR-06, FR-14, #1554, IADR-0622: ボットの許可表（ボットの REST アダプタが呼ぶ経路）。経路の引数（<c>{…}</c>）の中身は比べない。
/// 母集合の引き方は仕様書 20261106_1554_owner-bot-scope（ボットのアダプタの <c>/</c> で始まる文字列リテラルを全部引く）。
/// </summary>
public static class OwnerBotRoutes
{
    public static IReadOnlyList<OwnerBotRoute> All { get; } =
    [
        // リスク管理
        new("POST", "/risk-controls/kill-switch/engage"),
        new("POST", "/risk-controls/kill-switch/disengage"),
        new("POST", "/risk-controls/pause"),
        new("POST", "/risk-controls/resume"),
        new("GET", "/risk-controls/status"),
        new("GET", "/risk-controls/stage-gate"),
        new("POST", "/risk-controls/stage-gate/transition"),
        new("POST", "/risk-controls/stage-gate/withdrawal/evaluate"),
        new("POST", "/risk-controls/good-faith-violations/clear"),
        new("POST", "/risk-controls/position-drift/adopt"),

        // 報告書
        new("GET", "/reports/{periodKey}/review"),
        new("GET", "/reports/period-keys"),
        new("GET", "/reports"),
        new("POST", "/reports/{periodKey}/confirm"),
        new("POST", "/reports/{periodKey}/request-changes"),
        new("POST", "/reports/{periodKey}/regenerate"),
        new("POST", "/reports/policy-revisions"),
        new("GET", "/reports/policy-revisions/watchlist-proposal"),
        new("POST", "/reports/policy-revisions/{attemptId}/watchlist-apply-result"),

        // 市場監視
        new("GET", "/monitor/watchlist"),
        new("POST", "/monitor/watchlist/proposal-apply"),
    ];

    /// <summary>経路の雛形を比べる形にする（引数の中身・制約を落とし、末尾の / を除く）。</summary>
    public static string Normalize(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var builder = new System.Text.StringBuilder(template.Length);
        var depth = 0;
        foreach (var c in template)
        {
            if (c == '{')
            {
                if (depth++ == 0)
                    builder.Append("{}");
                continue;
            }

            if (c == '}')
            {
                depth--;
                continue;
            }

            if (depth == 0)
                builder.Append(c);
        }

        var text = builder.ToString();
        var query = text.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
            text = text[..query];
        text = text.TrimEnd('/');
        return text.StartsWith('/') ? text : "/" + text;
    }
}

/// <summary>NFR-06, #1554, IADR-0622: 許可表の 1 行（HTTP メソッドと経路の雛形）。</summary>
public sealed record OwnerBotRoute(string Method, string Template)
{
    public string NormalizedTemplate => OwnerBotRoutes.Normalize(Template);
}
