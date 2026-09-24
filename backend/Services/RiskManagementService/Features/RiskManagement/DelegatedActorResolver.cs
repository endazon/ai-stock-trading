using System.Security.Claims;
using System.Text.RegularExpressions;

namespace RiskManagementService.Features.RiskManagement;

// FR-20, FR-11, FR-14, UC-06, ADR-0003, ADR-0008, IADR-0062 決定3/決定4, IADR-0240 決定11, IADR-0383, #868:
// **操作者（誰が操作したか）と認可の主体（誰の資格で通ったか）の解決。純関数。**
//
// Discord Bot は owner マップ機密クライアント（`client_credentials`・IADR-0062 決定4 / IADR-0098）のトークンで
// リスク管理の OwnerOnly エンドポイントを呼ぶ。**そのトークンの主体は人ではない**ため、`Identity.Name` から採る
// 承認者は名前クレームが無ければ `unknown`、在っても `service-account-<clientId>` になる。段階遷移は **FR-20 の
// 実資金ゲートの承認記録**であり、これが `unknown` では 7 年保持の台帳（FR-11）の意味が失われる（#868 の起点）。
//
// Bot は多層認証で解決した利用者を要求本文（OnBehalfOf）で運ぶ。kill switch / pause / GFV が理由欄
// （`…（actor=<利用者>）`）で運ぶのと同じ作法を、**理由欄を持たない操作では構造化した欄で**行う
// （計画 MSP:ADR-0086 決定1「利用者文脈は本文で運ぶ」と同じ向き）。
//
// 🔴 **本文の名前をそのまま信じない。** 信じるのは「トークンの `azp` が、構成で宣言した信頼クライアントに一致する」
// ときだけである。当該クライアントは機密（secret 必須）・`standardFlowEnabled:false`・
// `directAccessGrantsEnabled:false`（IADR-0098 決定1）であり、**利用者のトークンの `azp` にはなり得ない**
// ——OnBehalfOf を効かせられるのは、そのクライアントの secret を持つ者（Bot）だけである。
//   - 利用者トークン直叩き・一覧外のクライアント・`azp` の欠落: OnBehalfOf を**無視**する（呼び出し自体は通し、
//     操作者はトークンの主体。無視したことは警告ログへ残す）。
//   - 信頼一覧が空（既定）: 誰の OnBehalfOf も信じない（fail-safe。設定漏れで信頼を開かない）。
//   - 信頼クライアントが値域外の値を送った: **拒否**（操作者を記録できない操作は行わない）。
//
// 本型は IADR-0240 の `ReportService.Features.Reports.ConfirmReport.ConfirmingActorResolver` と**同じ規律の写し**である。
// サービス間でコードは共有しない（`Shared.Contracts` は契約だけを持ち、認可の解釈は各サービスの関心）。
// 🔴 **値域（OnBehalfOfPattern）は 2 サービスに同じ形で存在する。** 片方だけ変えると、送り手（Bot）が値域内と
// 判定した名前を受け手が 400 で弾く、という静かな破れが起きる。変えるときは両方を変える（機械検査は無い）。
public static partial class DelegatedActorResolver
{
    // Keycloak のアクセストークンが常に持つ authorized party。client_credentials では当該クライアント ID。
    public const string AuthorizedPartyClaim = "azp";

    // 名前も azp も無いトークンの最終の倒し先（`RiskControlEndpoints.ActorOf` と同じ既定値）。
    // 🔴 **段階遷移ではこの値を通さない**（呼び出し側が `IsUnidentified` で 400 に倒す）。
    public const string Unknown = "unknown";

    // Keycloak 利用者名の値域。**`\A…\z`**（.NET の `$` は末尾 LF の直前にもマッチする。IADR-0240 決定6 の追記）。
    // 台帳・通知・監査要約へそのまま載るため、空白・改行・マークダウンの記号を通さない。
    // 🔴 **Discord のメンションは塞げていない**（メール形式の利用者名のため `@` を許している）。値を送れるのは
    // owner クライアントの secret を持つ者だけで、出所は運用者設定の UserMapping であり権限昇格にはならない。
    // 塞ぐのは送信側（`allowed_mentions`・IADR-0359）の仕事である。
    [GeneratedRegex(@"\A[A-Za-z0-9._@+-]{1,64}\z")]
    private static partial Regex OnBehalfOfPattern();

    public static DelegatedActor Resolve(
        ClaimsPrincipal user, string? onBehalfOf, IReadOnlySet<string> trustedClientIds)
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

        // 代理は成立していない。操作者はトークンの主体: 名前 → client:<azp> → unknown の順に倒す。
        // **unknown の前に client:<azp> を挟む**——人は分からなくても、誰の資格で操作されたかは残せる
        // （IADR-0240 決定11 と同じ倒し方）。
        var actor = user.Identity?.Name is { Length: > 0 } name ? name
            : client is { Length: > 0 } ? $"client:{client}"
            : Unknown;

        return new DelegatedActor(actor, AuthorizedBy: null, IgnoredOnBehalfOf: onBehalfOf is not null, Rejected: false);
    }

    // 構成値（カンマ区切り。Bot 側 AllowedUserIds と同じ書式）を信頼一覧へ。空・空白は「誰も信じない」。
    public static IReadOnlySet<string> ParseTrustedClientIds(string? configured) =>
        (configured ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
}

// Actor             = 操作した利用者（段階遷移では承認者＝`StageTransition.ApprovedBy`）。
// AuthorizedBy      = 代理のときの認可の主体（信頼クライアントの ID）。代理でなければ null。
// IgnoredOnBehalfOf = 本文に代理指定が在ったが信じなかった（なりすましの試行／設定漏れが見えるようログに残す）。
// Rejected          = 信頼クライアントの代理指定が値域外。操作を行わない（400）。
public sealed record DelegatedActor(string Actor, string? AuthorizedBy, bool IgnoredOnBehalfOf, bool Rejected)
{
    /// <summary>
    /// FR-20, FR-11, IADR-0062 決定3「actor を特定できない操作はさせない」, IADR-0383 決定3:
    /// **操作者をまったく特定できない**（名前クレームも <c>azp</c> も無いトークン）。
    /// <para>
    /// 段階遷移はこの状態で**実行してはならない**。<c>StageGate.RequestTransition</c> の承認者検査は
    /// 空文字だけを見るため <c>unknown</c> は素通りし、実資金ゲートの承認記録が「誰が承認したか不明」のまま
    /// 7 年残る（#868 の症状そのもの）。
    /// </para>
    /// </summary>
    public bool IsUnidentified => Actor is DelegatedActorResolver.Unknown or "" || string.IsNullOrWhiteSpace(Actor);
}

// FR-20, FR-11, IADR-0240 決定11, IADR-0383: 代理（OnBehalfOf）を信じてよいクライアント ID の一覧。
// **既定は空＝誰も信じない**（fail-safe）。構成 `RiskControls:DelegatedActor:TrustedClientIds`（カンマ区切り）。
//
// 🔴 通知サービスの `Notifications:Discord:OwnerAuth:ClientId` と**同じ秘密鍵**
// （`ast-secrets/discord-owner-auth-client-id`）から注入する（単一情報源。片方だけ変えると代理が黙って
// 不成立になり、承認者が `client:<azp>` へ落ちる）。報告書サービスの `Reports:DelegatedActor:TrustedClientIds`
// も同じ鍵を使う。
public sealed record DelegatedActorOptions(IReadOnlySet<string> TrustedClientIds)
{
    public const string TrustedClientIdsKey = "RiskControls:DelegatedActor:TrustedClientIds";
}
