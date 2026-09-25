using System.Security.Claims;
using System.Text.RegularExpressions;

namespace MarketMonitorService.Features.MarketMonitor;

// FR-13, FR-14, ADR-0042 決定 1, #1025, IADR-0433 決定 3: 操作者（誰が操作したか）と認可の主体（誰の資格で通ったか）の解決。純関数。
//
// Discord Bot は owner マップ機密クライアント（client_credentials・IADR-0062 決定4 / IADR-0098）のトークンで呼ぶため、
// トークンの主体は人ではない。`/policy` の入れ替え案を適用するとき、変更者を**本人**として変更履歴に残すため（FR-13 と
// 同じ監査・ADR-0042 決定 1）、Bot は多層認証で解決した利用者を本文（OnBehalfOf）で運ぶ。
//
// 🔴 本文の名前は「トークンの azp が構成の信頼クライアントに一致する」ときだけ信じる（報告書の ConfirmingActorResolver・
// リスク管理の DelegatedActorResolver と同じ規律の写し。サービス間でコードは共有しない）。
//   - 利用者トークン直叩き・一覧外のクライアント: OnBehalfOf を無視する（操作者はトークンの主体）。
//   - 信頼一覧が空（既定）: 誰の OnBehalfOf も信じない（fail-safe）。
//   - 信頼クライアントが値域外の値を送った: 拒否（変更者を記録できない変更は行わない）。
// 🔴 値域（OnBehalfOfPattern）は 3 サービスに同じ形で存在する。変えるときは全部を変える（機械検査は無い）。
public static partial class DelegatedActorResolver
{
    public const string AuthorizedPartyClaim = "azp";

    public const string Unknown = "unknown";

    [GeneratedRegex(@"\A[A-Za-z0-9._@+-]{1,64}\z")]
    private static partial Regex OnBehalfOfPattern();

    public static DelegatedActor Resolve(ClaimsPrincipal user, string? onBehalfOf, IReadOnlySet<string> trustedClientIds)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(trustedClientIds);

        var client = user.FindFirst(AuthorizedPartyClaim)?.Value;
        var isTrustedClient = client is { Length: > 0 } && trustedClientIds.Contains(client);

        if (isTrustedClient && onBehalfOf is not null)
        {
            return OnBehalfOfPattern().IsMatch(onBehalfOf)
                ? new DelegatedActor(onBehalfOf, AuthorizedBy: client, IgnoredOnBehalfOf: false, Rejected: false)
                : new DelegatedActor(string.Empty, AuthorizedBy: null, IgnoredOnBehalfOf: false, Rejected: true);
        }

        // 代理は成立していない。操作者はトークンの主体: 名前 → client:<azp> → unknown の順に倒す（既存の ActorOf と同じ既定）。
        var actor = user.Identity?.Name is { Length: > 0 } name ? name
            : client is { Length: > 0 } ? $"client:{client}"
            : Unknown;

        return new DelegatedActor(actor, AuthorizedBy: null, IgnoredOnBehalfOf: onBehalfOf is not null, Rejected: false);
    }

    // 構成値（カンマ区切り）を信頼一覧へ。空・空白は「誰も信じない」。
    public static IReadOnlySet<string> ParseTrustedClientIds(string? configured) =>
        (configured ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
}

// Actor = 変更者。AuthorizedBy = 代理のときの認可の主体（信頼クライアントの ID）。
// IgnoredOnBehalfOf = 代理指定を信じなかった。Rejected = 信頼クライアントの代理指定が値域外（400）。
public sealed record DelegatedActor(string Actor, string? AuthorizedBy, bool IgnoredOnBehalfOf, bool Rejected);

// 代理を信じてよいクライアント ID の一覧。**既定は空＝誰も信じない**。構成 `Monitor:DelegatedActor:TrustedClientIds`。
// 🔴 通知サービスの owner クライアント ID（`ast-secrets/discord-owner-auth-client-id`）と同じ秘密鍵から注入する
// （報告書の `Reports:…`・リスク管理の `RiskControls:…` と同じ単一情報源）。
public sealed record DelegatedActorOptions(IReadOnlySet<string> TrustedClientIds)
{
    public const string TrustedClientIdsKey = "Monitor:DelegatedActor:TrustedClientIds";
}
