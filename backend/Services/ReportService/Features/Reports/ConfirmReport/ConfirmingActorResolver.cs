using System.Security.Claims;
using System.Text.RegularExpressions;

namespace ReportService.Features.Reports.ConfirmReport;

// FR-09, FR-07, UC-03, ADR-0003, IADR-0240 決定11, #774: 確定者（誰が確定を操作したか）の解決。純関数。
//
// Discord Bot は owner マップ機密クライアント（client_credentials・IADR-0062 決定4 / IADR-0098）のトークンで確定を
// 呼ぶ。**そのトークンの主体は人ではない**ため、トークンの名前だけを見ると確定者が `unknown`（名前クレーム無し）か
// service account 名になり、通知と監査台帳の両方に誤った確定者が残る。Bot は多層認証で解決した利用者を本文
// （OnBehalfOf）で運ぶ（kill switch / pause / GFV が理由欄で運ぶのと同じ作法＝計画 MSP:ADR-0086 決定1）。
//
// 🔴 **本文の名前をそのまま信じない。** 信じるのは「トークンの azp が、構成で宣言した信頼クライアントに一致する」
// ときだけである。当該クライアントは機密（secret 必須）・standardFlow / directAccessGrants 無効であり、利用者の
// トークンの azp にはなり得ない——**OnBehalfOf を効かせられるのは、そのクライアントの secret を持つ者（Bot）だけ**。
//   - 利用者トークン直叩き・一覧外のクライアント: OnBehalfOf を**無視**する（確定は通す。確定者はトークンの主体）。
//   - 信頼一覧が空（既定）: 誰の OnBehalfOf も信じない（fail-safe。設定漏れで信頼を開かない）。
//   - 信頼クライアントが値域外の値を送った: **拒否**（確定者を記録できない確定は行わない）。
public static partial class ConfirmingActorResolver
{
    // Keycloak のアクセストークンが常に持つ authorized party。client_credentials では当該クライアント ID。
    public const string AuthorizedPartyClaim = "azp";

    // 名前も azp も無いトークンの最終の倒し先（従来の既定値。Keycloak のトークンでは起きない）。
    public const string Unknown = "unknown";

    // Keycloak 利用者名の値域。**`\A…\z`**（.NET の `$` は末尾 LF の直前にもマッチする。IADR-0240 決定6 の追記）。
    // 通知本文・監査要約へそのまま載るため、空白・改行・マークダウンの記号を通さない。
    // 🔴 **Discord のメンションは塞げていない。** メール形式の利用者名のために `@` を許しているので、
    // `@everyone` / `@here` は値域を通り、通知本文へそのまま出る（#861 の監査が実測）。この値を送れるのは
    // owner クライアントの secret を持つ者だけで、出所は運用者設定の UserMapping なので権限昇格にはならないが、
    // 塞ぐのは送信側（Webhook の `allowed_mentions`）の仕事である——#867。
    [GeneratedRegex(@"\A[A-Za-z0-9._@+-]{1,64}\z")]
    private static partial Regex OnBehalfOfPattern();

    public static ConfirmingActor Resolve(
        ClaimsPrincipal user, string? onBehalfOf, IReadOnlySet<string> trustedClientIds)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(trustedClientIds);

        var client = user.FindFirst(AuthorizedPartyClaim)?.Value;
        var isTrustedClient = client is { Length: > 0 } && trustedClientIds.Contains(client);

        if (isTrustedClient && onBehalfOf is not null)
        {
            return OnBehalfOfPattern().IsMatch(onBehalfOf)
                ? new ConfirmingActor(onBehalfOf, AuthorizedBy: client, IgnoredOnBehalfOf: false, Rejected: false)
                : new ConfirmingActor(string.Empty, AuthorizedBy: null, IgnoredOnBehalfOf: false, Rejected: true);
        }

        // 代理は成立していない。確定者はトークンの主体: 名前 → client:<azp> → unknown の順に倒す。
        // **unknown の前に client:<azp> を挟む**——人は分からなくても、誰の資格で確定されたかは残せる。
        var actor = user.Identity?.Name is { Length: > 0 } name ? name
            : client is { Length: > 0 } ? $"client:{client}"
            : Unknown;

        return new ConfirmingActor(actor, AuthorizedBy: null, IgnoredOnBehalfOf: onBehalfOf is not null, Rejected: false);
    }

    // 構成値（カンマ区切り。Bot 側 AllowedUserIds と同じ書式）を信頼一覧へ。空・空白は「誰も信じない」。
    public static IReadOnlySet<string> ParseTrustedClientIds(string? configured) =>
        (configured ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
}

// Actor            = 確定者（ReportConfirmed.Actor）。
// AuthorizedBy     = 代理確定のときの認可の主体（信頼クライアントの ID）。代理でなければ null。
// IgnoredOnBehalfOf= 本文に代理指定が在ったが信じなかった（なりすましの試行が見えるようログに残す）。
// Rejected         = 信頼クライアントの代理指定が値域外。確定しない（400）。
public sealed record ConfirmingActor(string Actor, string? AuthorizedBy, bool IgnoredOnBehalfOf, bool Rejected);

// 信頼するクライアント ID の一覧（構成 `Reports:DelegatedActor:TrustedClientIds`）。既定は空＝誰も信じない。
public sealed record DelegatedActorOptions(IReadOnlySet<string> TrustedClientIds)
{
    public const string TrustedClientIdsKey = "Reports:DelegatedActor:TrustedClientIds";
}
