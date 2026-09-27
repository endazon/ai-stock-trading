using System.Security.Claims;
using Microsoft.Extensions.Configuration;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;

// NFR-06, FR-14, ADR-0047 決定 3, IADR-0284, IADR-0448 決定 1〜3, #1067 (#753):
// **east-west gRPC 面の所有者の門で、`trading-owner` を持つトークンの呼び出し元のクライアント（`azp`）を確かめる。**
//
// 🔴 **なぜ要るか**: Discord ボットのトークン（owner マップの機密クライアント `ai-stock-trading-owner` が
// client_credentials で取る。IADR-0062 決定 4 / IADR-0098）は人の利用者と**同じ** `trading-owner` を持つ。
// ロールだけを見る `OwnerOrService` では、人の利用者のトークン（BFF のセッションなら `azp` は BFF の client）が
// gRPC のメタデータに載っても門を通ってしまう（ADR-0047 実測 5）。gRPC 面は east-west（サービス自身の資格情報）の面であり、
// 人の利用者はエッジ（BFF が中継する REST）で操作する。
//
// 判定（`GrpcOwnerOrService` ポリシー。`AuthExtensions` が登録する）:
//   - サービスの分岐（`trading-service`）: **従来どおり**（`azp` を見ない。ADR-0047 決定 3「s2s の分岐は変えない」）。
//   - 所有者の分岐（`trading-owner`）: `azp` クレームが**ちょうど 1 つ**あり、その**生の値**が許可集合に**序数一致**で在るときだけ真。
//     🔴 前後空白を落とさない・大小文字を畳まない・接頭辞で比べない（変種は別のクライアントである）。
//     🔴 `azp` が無い・空・2 つ以上なら偽（`preferred_username` の `service-account-<id>` から復元しない —— 名前は人が選べる）。
//
// 許可集合（構成 `Auth:GrpcOwnerClients`。配列）:
//   - 🔴 **未構成なら既定 = ボットの機密クライアント `ai-stock-trading-owner`**（realm `infra/keycloak/realm-export.json` の
//     owner クライアント・compose の `DISCORD_OWNERAUTH_CLIENTID` 既定。`GrpcOwnerGateWiringTests` が固定する）。
//   - **構成すると既定を置き換える**（足し合わせない）。構成の側は前後空白を落とし、空白だけの要素は捨てる。**空なら誰の所有者トークンも通さない**（fail-closed）。
//   - 🔴 **1 つの値（配列でない）で書かれていたら起動時に例外**（`Auth__GrpcOwnerClients=a,b` は配列へ束縛されず、既定へ静かに戻る。
//     基盤 `TrustedUserContextRelay.ThrowIfScalar` と同じ形）。配列は `Auth__GrpcOwnerClients__0` / `__1` … で書く。
//
// 🔴 **REST の面の所有者の判定は変えない**（`OwnerOrService` のまま。人の利用者は BFF が中継する REST で操作する。ADR-0047 決定 3 は gRPC 面だけ）。
public static class GrpcOwnerClientGate
{
    /// <summary>許可集合を持つ構成キー（配列）。</summary>
    public const string ClientsKey = "Auth:GrpcOwnerClients";

    /// <summary>トークンの呼び出し元のクライアント（authorized party）のクレーム名。</summary>
    public const string AuthorizedPartyClaim = "azp";

    /// <summary>未構成のときの許可集合。Discord ボットの機密クライアントだけ。</summary>
    public static IReadOnlyList<string> DefaultClients { get; } = ["ai-stock-trading-owner"];

    /// <summary>
    /// 構成から実際に使う許可集合を解決する。**先に <see cref="ThrowIfScalar"/> で形を確かめる。**
    /// 子要素が 1 つも無ければ未構成（既定）。在れば既定を置き換え、前後空白を落として空白だけの要素を捨てる。
    /// </summary>
    public static IReadOnlyList<string> EffectiveClients(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ThrowIfScalar(configuration);

        var children = configuration.GetSection(ClientsKey).GetChildren().ToList();
        if (children.Count == 0)
            return DefaultClients;

        return children
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 構成の形を確かめる。<c>Auth:GrpcOwnerClients</c> が配列ではなく 1 つの値（空文字を含む）で書かれていたら例外を投げる。
    /// </summary>
    public static void ThrowIfScalar(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.GetSection(ClientsKey).Value is not null)
            throw new InvalidOperationException(
                $"{ClientsKey} は配列で構成すること（環境変数なら Auth__GrpcOwnerClients__0={DefaultClients[0]}）。"
                + $"1 つの値（カンマ区切りを含む）は配列へ束縛されず、既定の {DefaultClients[0]} へ戻ってしまう。");
    }

    /// <summary>
    /// 所有者のトークンの呼び出し元のクライアントが許可集合に在るか。<c>azp</c> がちょうど 1 つ・生の値が序数一致のときだけ真。
    /// </summary>
    public static bool IsTrustedOwnerClient(ClaimsPrincipal? user, IReadOnlyList<string> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);
        if (user is null) return false;

        using var azps = user.FindAll(AuthorizedPartyClaim).GetEnumerator();
        if (!azps.MoveNext()) return false;
        var azp = azps.Current.Value;
        if (azps.MoveNext()) return false;          // 2 つ以上は曖昧 → 通さない
        if (string.IsNullOrEmpty(azp)) return false;

        foreach (var client in clients)
        {
            if (string.Equals(client, azp, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// gRPC 面の門（<c>GrpcOwnerOrService</c>）の判定。サービス（<c>trading-service</c>）は従来どおり、
    /// 所有者（<c>trading-owner</c>）は呼び出し元のクライアントが許可集合に在るときだけ通す。
    /// </summary>
    public static bool Allows(ClaimsPrincipal? user, IReadOnlyList<string> clients)
    {
        if (user?.Identity?.IsAuthenticated != true) return false;

        if (user.IsInRole(Extensions.AiStockTradingAuthPolicies.ServiceRole))
            return true;

        return AllowsOwner(user, clients);
    }

    /// <summary>
    /// 所有者限定の gRPC 面の門（<c>GrpcOwnerOnly</c>。IADR-0449 決定 2）の判定。所有者（<c>trading-owner</c>）で、呼び出し元のクライアントが
    /// 許可集合に在るときだけ真。**サービス（<c>trading-service</c>）の分岐は持たない**（REST の <c>OwnerOnly</c> と同じく s2s には開かない）。
    /// <see cref="Allows"/> の所有者の分岐と同じ判定である（2 箇所に書かない）。
    /// </summary>
    public static bool AllowsOwner(ClaimsPrincipal? user, IReadOnlyList<string> clients)
    {
        if (user?.Identity?.IsAuthenticated != true) return false;

        return user.IsInRole(Extensions.AiStockTradingAuthPolicies.OwnerRole)
            && IsTrustedOwnerClient(user, clients);
    }
}
