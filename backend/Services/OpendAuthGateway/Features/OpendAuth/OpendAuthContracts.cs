using System.Text.Json.Serialization;

namespace OpendAuthGateway.Features.OpendAuth;

/// <summary>
/// #722: <c>POST /opend-auth/verify</c> の要求本文。
/// <para>
/// 🔴 <b>コマンド文字列を受け取る欄は無い。</b> 受け取るのは閉じた列挙（<c>phone</c> / <c>pic</c> /
/// <c>resend</c>）とコードだけで、OpenD へ渡る行は <see cref="OpendConsoleCommand"/> が組み立てる。
/// </para>
/// </summary>
/// <param name="Kind"><c>phone</c> / <c>pic</c> / <c>resend</c> のいずれか。</param>
/// <param name="Code">検証コード。<c>resend</c> では指定しない。</param>
public sealed record VerifyRequest(
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("code")] string? Code);

/// <summary>
/// #722: <c>POST /opend-auth/verify</c> の応答。
/// <b>投入したコードは載せない</b>（要求と同じ値でも返さない。IADR-0320 決定 5）。
/// </summary>
/// <param name="Status">常に <c>accepted</c>（受理できなかった場合は本文ごと別の形になる）。</param>
/// <param name="Kind">受理した操作の種別。</param>
public sealed record VerifyAccepted(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("kind")] string Kind);

/// <summary>
/// #722: <c>GET /opend-auth/state</c> の応答。
/// </summary>
/// <param name="Prompt">
/// いま OpenD が待っている入力（<c>phone</c> / <c>pic</c> / <c>resend</c>）。判定できなければ <c>null</c>。
/// </param>
/// <param name="CaptchaAvailable">画像 CAPTCHA の写しが取得できる状態か。</param>
/// <param name="ConsoleAvailable">コンソール複製が読める状態か（OpenD 未起動なら <c>false</c>）。</param>
/// <param name="ConsoleTail">整形済みのコンソール末尾（上限バイト数で切り、資格情報は伏せてある）。</param>
public sealed record OpendAuthState(
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("captchaAvailable")] bool CaptchaAvailable,
    [property: JsonPropertyName("consoleAvailable")] bool ConsoleAvailable,
    [property: JsonPropertyName("consoleTail")] string ConsoleTail);

/// <summary>
/// #722: 受け付けなかったときの応答。<b>理由の符号だけを返し、入力そのものは返さない</b>。
/// </summary>
/// <param name="Error">機械可読な理由（<see cref="VerifyRejection"/> 由来 / <c>rate_limited</c> 等）。</param>
public sealed record OpendAuthError(
    [property: JsonPropertyName("error")] string Error);
