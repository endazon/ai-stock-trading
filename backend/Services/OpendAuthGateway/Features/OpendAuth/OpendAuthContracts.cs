using System.Text.Json.Serialization;

namespace OpendAuthGateway.Features.OpendAuth;

/// <summary>
/// #722: <c>POST /opend-auth/verify</c> の要求本文。
/// <para>
/// 🔴 <b>受け取るのはコードだけである。</b> コマンド文字列も種別も受け取らない ——
/// <b>どのコマンドを送るかは、OpenD がいま待っているプロンプトからサーバが決める</b>
/// （planning#594 の裁定: 「画面に自由入力のコンソールは置かず、送信するコマンドは
/// サーバ側が待機中のプロンプト種別から決める。利用者はコマンドを選べない」）。
/// </para>
/// <para>
/// 種別を要求で受けると、待機中のプロンプトと食い違う組み合わせを呼び出し側が作れてしまう。
/// 判定の源を 1 つ（コンソールの複製）に絞れば、その食い違い自体が存在しなくなる。
/// </para>
/// </summary>
/// <param name="Code">検証コード。</param>
public sealed record VerifyRequest(
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
/// <para>
/// 🔴 <b>3 状態を潰さずに描き分けられる形にしてある</b>（planning#594 の裁定）。
/// 画面はこの 2 欄を組にして読むこと。
/// </para>
/// <list type="table">
///   <item><description><c>consoleAvailable=false</c> …… <b>供給が無い</b>（状態を取得できていない）</description></item>
///   <item><description><c>consoleAvailable=true</c> かつ <c>prompt=null</c> …… <b>対象なし</b>（いま入力を待っていない）</description></item>
///   <item><description><c>consoleAvailable=true</c> かつ <c>prompt</c> あり …… 入力待ち</description></item>
/// </list>
/// <para>
/// 前 2 者を取り違えると<b>正常にログインできているように見える</b>ため、
/// 「値が無い」を一括りにしないこと。
/// </para>
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
