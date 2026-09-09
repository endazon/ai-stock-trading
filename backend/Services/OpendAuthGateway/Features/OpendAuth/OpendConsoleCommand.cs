using System.Text.RegularExpressions;

namespace OpendAuthGateway.Features.OpendAuth;

/// <summary>#722, IADR-0322: 利用者が選べる操作。<b>閉じた列挙であり、これ以外は存在しない。</b></summary>
public enum VerifyKind
{
    /// <summary>SMS の検証コードを入れる（<c>input_phone_verify_code -code=&lt;code&gt;</c>）。</summary>
    Phone,

    /// <summary>画像 CAPTCHA の 4 文字を入れる（<c>input_pic_verify_code -code=&lt;code&gt;</c>）。</summary>
    Pic,

    /// <summary>SMS の再送を要求する（<c>req_phone_verify_code</c>。引数を取らない）。</summary>
    Resend,
}

/// <summary>#722, IADR-0322: 受け付けなかった理由。<b>入力そのものは決して載せない</b>（応答にもログにも）。</summary>
public enum VerifyRejection
{
    /// <summary>棄却なし。</summary>
    None,

    /// <summary><c>kind</c> が閉じた列挙のどれでもない。</summary>
    UnknownKind,

    /// <summary>コードが要る種別なのに空である。</summary>
    MissingCode,

    /// <summary>コードを取らない種別（<c>resend</c>）にコードが付いている。</summary>
    UnexpectedCode,

    /// <summary>コードが長すぎる。</summary>
    CodeTooLong,

    /// <summary>コードが許された文字種・桁数に一致しない（制御文字・非 ASCII 数字・前後空白を含む）。</summary>
    MalformedCode,
}

/// <summary>
/// #722, IADR-0322 決定 1: <b>OpenD のコンソールへ書いてよい行を組み立てる唯一の場所</b>。
/// <para>
/// 🔴 <b>クライアントはコマンド文字列を渡さない。</b> 渡すのは閉じた列挙（<c>phone</c> / <c>pic</c> /
/// <c>resend</c>）とコードだけで、実際に書かれる行は<b>本クラスが組み立てる</b>。
/// 書かれ得る行は次の 3 つで、それ以外は<b>この型では表現できない</b>。
/// </para>
/// <list type="number">
///   <item><c>input_phone_verify_code -code=&lt;4〜8 桁の ASCII 数字&gt;</c></item>
///   <item><c>input_pic_verify_code -code=&lt;4 文字の ASCII 英数&gt;</c></item>
///   <item><c>req_phone_verify_code</c>（引数なし）</item>
/// </list>
/// <para>
/// なぜここまで締めるか: OpenD のコンソールは検証コード以外も受け付ける。
/// <c>show_delay_report -detail_report_path=&lt;path&gt;</c> と
/// <c>show_sub_info -sub_info_path=&lt;path&gt;</c> は <b>root 権限で呼び出し側が選んだパスへファイルを書く</b>
/// （デバイス信頼の実体 <c>Device.dat</c> や <c>OpenD.xml</c> を潰せる）。<c>relogin -login_pwd=</c> は
/// 信頼済み端末からのパスワード試行になり、<c>exit</c> は口座への窓口を落とす。
/// <b>濾過されない 1 行が通れば、それだけで実口座に対する重大な事故になる。</b>
/// </para>
/// <para>
/// 🔴 <b>アンカーは <c>\A</c> / <c>\z</c> を使う（<c>^</c> / <c>$</c> ではない）。</b>
/// .NET の <c>$</c> は<b>末尾の <c>\n</c> の直前にも一致する</b>ため、<c>^[0-9]{4,8}$</c> は
/// <c>"123456\n"</c> を通してしまう —— それは<b>改行注入がそのまま素通りする</b>ということである。
/// </para>
/// <para>
/// 🔴 <b><c>\d</c> を使わない。</b> .NET の <c>\d</c> は Unicode の数字（全角 <c>１２３４</c>・
/// アラビア数字 <c>٤٥٦٧</c> 等）にも一致する。OpenD が受け取るのは ASCII であり、
/// <c>[0-9]</c> と書けば ASCII だけに閉じる。
/// </para>
/// </summary>
public static partial class OpendConsoleCommand
{
    /// <summary>コードの最大長（<c>phone</c> の 8 桁が最長）。これを超える入力は正規表現に掛ける前に落とす。</summary>
    public const int MaxCodeLength = 8;

    /// <summary>SMS 検証コード: ASCII 数字 4〜8 桁ちょうど。</summary>
    [GeneratedRegex(@"\A[0-9]{4,8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneCodePattern();

    /// <summary>画像 CAPTCHA: ASCII 英数 4 文字ちょうど。</summary>
    [GeneratedRegex(@"\A[A-Za-z0-9]{4}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PicCodePattern();

    /// <summary>
    /// 要求の <c>kind</c>（文字列）を閉じた列挙へ写す。
    /// <b>大文字小文字を区別する。</b> 受け口を緩めても得るものが無く、
    /// 「どの綴りが通るか」の面が広がるだけである。
    /// </summary>
    public static bool TryParseKind(string? kind, out VerifyKind parsed)
    {
        switch (kind)
        {
            case "phone": parsed = VerifyKind.Phone; return true;
            case "pic": parsed = VerifyKind.Pic; return true;
            case "resend": parsed = VerifyKind.Resend; return true;
            default: parsed = default; return false;
        }
    }

    /// <summary>
    /// 検証済みの入力から、OpenD のコンソールへ書く<b>1 行</b>（末尾 <c>\n</c> 込み）を組み立てる。
    /// 棄却したときは <paramref name="line"/> を空文字列にする（<b>呼び出し側は何も書かない</b>）。
    /// </summary>
    /// <param name="kind">閉じた列挙の文字列表現（<c>phone</c> / <c>pic</c> / <c>resend</c>）。</param>
    /// <param name="code">検証コード。<c>resend</c> では null / 空でなければならない。</param>
    /// <param name="line">組み立てた 1 行。棄却時は空文字列。</param>
    /// <param name="rejection">棄却の理由。受理時は <see cref="VerifyRejection.None"/>。</param>
    public static bool TryCompose(string? kind, string? code, out string line, out VerifyRejection rejection)
    {
        line = string.Empty;

        if (!TryParseKind(kind, out var parsed))
        {
            rejection = VerifyRejection.UnknownKind;
            return false;
        }

        return TryCompose(parsed, code, out line, out rejection);
    }

    /// <summary>
    /// #722 / planning#594: <b>種別が既に決まっているとき</b>の組み立て。
    /// <para>
    /// 本番の経路はこちらである —— 種別は<b>コンソールの複製から検出したプロンプト</b>であって、
    /// 呼び出し側の申告ではない。文字列を受ける上の多重定義は、綴りの検証そのものを固定する
    /// 試験のために残してある。
    /// </para>
    /// </summary>
    public static bool TryCompose(VerifyKind parsed, string? code, out string line, out VerifyRejection rejection)
    {
        line = string.Empty;

        if (parsed == VerifyKind.Resend)
        {
            // 再送は引数を取らない。コードが付いてくるのは呼び出し側の誤りであり、黙って捨てない。
            if (!string.IsNullOrEmpty(code))
            {
                rejection = VerifyRejection.UnexpectedCode;
                return false;
            }

            line = "req_phone_verify_code\n";
            rejection = VerifyRejection.None;
            return true;
        }

        if (string.IsNullOrEmpty(code))
        {
            rejection = VerifyRejection.MissingCode;
            return false;
        }

        // 長さは正規表現に掛ける前に切る（巨大入力を照合に流さない）。
        if (code.Length > MaxCodeLength)
        {
            rejection = VerifyRejection.CodeTooLong;
            return false;
        }

        var pattern = parsed == VerifyKind.Phone ? PhoneCodePattern() : PicCodePattern();
        if (!pattern.IsMatch(code))
        {
            // 制御文字（CR / LF / NUL）・非 ASCII 数字・前後の空白はすべてここで落ちる。
            rejection = VerifyRejection.MalformedCode;
            return false;
        }

        line = parsed == VerifyKind.Phone
            ? $"input_phone_verify_code -code={code}\n"
            : $"input_pic_verify_code -code={code}\n";
        rejection = VerifyRejection.None;
        return true;
    }
}
