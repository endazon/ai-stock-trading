using System.Diagnostics;
using OpenTelemetry;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Observability;

// FR-09, NFR（セキュリティ）, #751, IADR-0121, IADR-0333:
// **URI 自体が資格情報である送信先の URL を、トレース（Tempo）へ出す直前に落とす。**
//
// IADR-0121 はログ経路（Loki）を `RedactedUriHttpClientLogger` で塞いだが、同 ADR の「残存リスク」節が
// 記録したとおり `AddHttpClientInstrumentation()` の `url.full` タグには**フル URL（パス込み）**が載り続けていた。
// Discord Webhook のトークンは**クエリではなくパス**（/api/webhooks/<id>/<token>）にあるため、
// .NET 9 以降の既定のクエリ秘匿では覆われない。
//
// 🔴 **計装ではなくプロセッサで落とす。** .NET 8 以降の HTTP 計装はランタイム内蔵の System.Net.Http
// ActivitySource を購読する形であり、`url.full` を書くのは OTel ではなく**ランタイムの DiagnosticsHandler**
// （アクティビティ開始時）である。`EnrichWithHttpRequestMessage` に頼ると発火順が計装の内部実装に依存し、
// 計装の版が上がった瞬間に**無言で失効**し得る。OnEnd で最終状態を見れば、誰がタグを書いたかを問わない。
//
// 判定は**ホストではなくパスの形**で行う（下の CarriesCredentialInPath を参照）。
public sealed class CredentialBearingUriRedactionProcessor : BaseProcessor<Activity>
{
    /// <summary>OTel セマンティック規約（HTTP クライアント）でフル URL を持つタグ。</summary>
    internal const string UrlFullTag = "url.full";

    /// <summary>Discord Webhook のパス接頭辞。この先に &lt;id&gt;/&lt;token&gt; が続く。</summary>
    private const string WebhookPathPrefix = "/api/webhooks/";

    /// <summary>伏せた後の形。ログ側（RedactedUriHttpClientLogger）と同じ `scheme://host/***` に揃える。</summary>
    private const string RedactedPath = "/***";

    public override void OnEnd(Activity data)
    {
        if (data?.GetTagItem(UrlFullTag) is not string urlFull) return;
        if (!TryRedact(urlFull, out var redacted)) return;

        data.SetTag(UrlFullTag, redacted);
    }

    /// <summary>
    /// 資格情報を含む URI なら <c>scheme://host/***</c> を返す。そうでなければ <c>false</c>（原文を残す）。
    /// </summary>
    /// <remarks>
    /// 部分開示（トークンだけ伏せて id は出す）は**しない**。パスの構造は送信先の実装に依存し、
    /// 「どこまでが秘密か」をアプリ側が正しく知り続けられる保証がないため、ホストより先は一律に伏せる
    /// （IADR-0121 決定 5 と同じ判断）。userinfo（<c>https://user:pass@host/…</c>）も Host だけを使うことで落ちる。
    /// </remarks>
    public static bool TryRedact(string? urlFull, out string redacted)
    {
        redacted = string.Empty;
        if (string.IsNullOrEmpty(urlFull)) return false;
        if (!Uri.TryCreate(urlFull, UriKind.Absolute, out var uri)) return false;
        if (!CarriesCredentialInPath(uri)) return false;

        redacted = $"{uri.Scheme}://{uri.Host}{RedactedPath}";
        return true;
    }

    // 🔴 **ホスト（discord.com）では引かない。** ホストで一律に伏せると、Discord Bot Gateway
    // （FR-14 / IADR-0062・discord-owner-token クライアント）の API パスまで消える —— あちらの秘密は URL では
    // なくヘッダにあり、パスは障害切り分けに要る情報である。IADR-0121 決定 4「抑止は当該クライアントに閉じる」
    // をトレース側でも守る。パスの形で引けば、ホストが何であれ（試験のループバックでも）資格情報だけが落ちる。
    private static bool CarriesCredentialInPath(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (!path.StartsWith(WebhookPathPrefix, StringComparison.OrdinalIgnoreCase)) return false;

        // /api/webhooks/<id>/<token> ——「/api/webhooks/」の先が 2 セグメント以上あるものだけを資格情報とみなす。
        // /api/webhooks/<id> だけ（token を持たない形）は資格情報ではないため触らない。
        return path[WebhookPathPrefix.Length..]
            .Split('/', StringSplitOptions.RemoveEmptyEntries).Length >= 2;
    }
}
