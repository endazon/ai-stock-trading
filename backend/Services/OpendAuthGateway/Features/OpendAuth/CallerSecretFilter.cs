using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OpendAuthGateway.Common;

namespace OpendAuthGateway.Features.OpendAuth;

/// <summary>
/// NFR, ADR-0002, IADR-0322, IADR-0566: 呼び出し元の検証（共有シークレット）。多層防御の 1 段である
/// （もう 1 段は NetworkPolicy。どちらか一方の設定を誤っても、もう一方が残る）。
/// <para>
/// 4 本の口すべてに<b>グループのフィルタ 1 つ</b>で掛ける。端点ごとに書くと、次に端点を足した人が落とす。
/// </para>
/// <para>
/// 判定の順序と結果（いずれも<b>本文を読む前</b>・OpenD の標準入力へ 1 バイトも書かない）:
/// </para>
/// <list type="number">
///   <item>シークレットのファイルが読めない・空・短すぎる → <b>503 <c>caller_auth_not_configured</c></b>
///         （fail-closed。未構成を「誰でも通す」に倒さない）。</item>
///   <item>ヘッダーが無い・値が一致しない → <b>401 <c>unauthorized</c></b>（どちらかは返さない）。</item>
/// </list>
/// <para>
/// シークレットは<b>要求ごとにファイルから読む</b>。Secret の volume は kubelet が差し替えるので、
/// ESO が値を回しても OpenD の Pod を作り直さずに新しい値が効く（再起動は SMS 認証済みのセッションを切る）。
/// </para>
/// <para>
/// 🔴 シークレットの値・受け取ったヘッダーの値は<b>どこにも出さない</b>（ログ・応答とも）。
/// 比較は SHA-256 の要約どうしを <see cref="CryptographicOperations.FixedTimeEquals"/> で行い、長さの違いも時間差に出さない。
/// </para>
/// </summary>
public sealed class CallerSecretFilter(IOptions<OpendAuthOptions> options, ILogger<CallerSecretFilter> logger) : IEndpointFilter
{
    /// <summary>呼び出し元がシークレットを載せるヘッダー名（BFF 側 <c>OpendAuthUpstream.CallerSecretHeader</c> と対）。</summary>
    public const string HeaderName = "X-Opend-Auth-Caller-Secret";

    /// <summary>
    /// 受け入れるシークレットの最短の長さ（文字数）。これより短い値は「未構成」と同じに扱う
    /// （推測できる短い値で口を開けない）。
    /// </summary>
    public const int MinimumSecretLength = 32;

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = ReadSecret(options.Value.CallerSecretFile, out var reason);
        if (expected is null)
        {
            // 理由（未指定・ファイルが無い・読めない〔権限〕・短すぎる）だけを残す。値は出さない。
            logger.LogWarning(
                "opend-auth: 呼び出し元の検証に使うシークレットを使えないため、要求を受け付けない（理由={Reason}）。何も書いていない。", reason);
            return Results.Json(new OpendAuthError("caller_auth_not_configured"), statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var presented = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (!Matches(expected, presented))
        {
            logger.LogWarning("opend-auth: 呼び出し元の検証に失敗したため棄却した。何も書いていない。");
            return Results.Json(new OpendAuthError("unauthorized"), statusCode: StatusCodes.Status401Unauthorized);
        }

        return await next(context);
    }

    /// <summary>
    /// シークレットを読む。読めない・空・<see cref="MinimumSecretLength"/> 未満なら <c>null</c>（＝未構成）。
    /// 前後の空白・改行を落とす（<c>kubectl create secret --from-file</c> で末尾の改行が入りやすい。HTTP のヘッダーも
    /// 値の前後の空白を運ばないので、送り手の BFF も同じ正規化にそろえる）。
    /// <paramref name="reason"/> は <c>null</c> のときの理由（<c>not_set</c> / <c>missing</c> / <c>unreadable</c> / <c>too_short</c>）。
    /// <c>unreadable</c> は多くの場合ファイルの権限である（非 root 化したのに <c>fsGroup</c> が無い等）。
    /// </summary>
    public static string? ReadSecret(string? path, out string reason)
    {
        reason = "ok";
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "not_set";
            return null;
        }

        string raw;
        try
        {
            raw = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            reason = "missing";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = "unreadable";
            return null;
        }

        var value = raw.Trim();
        if (value.Length < MinimumSecretLength)
        {
            reason = "too_short";
            return null;
        }
        return value;
    }

    /// <summary>一定時間で比べる。空の提示は常に不一致。</summary>
    public static bool Matches(string expected, string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
