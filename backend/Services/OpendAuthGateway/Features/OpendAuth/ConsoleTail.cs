using System.Text;
using System.Text.RegularExpressions;

namespace OpendAuthGateway.Features.OpendAuth;

/// <summary>
/// #722, IADR-0320 決定 3: OpenD のコンソール複製（<c>script -q -f -a</c> の出力）から、
/// <b>画面へ出してよい末尾</b>と<b>いま待たれているプロンプト</b>を導く純関数。
/// <para>
/// コンソールは端末の記録であるため、そのままでは (a) ANSI エスケープ、(b) <c>\r</c> による行内上書き、
/// (c) NUL・制御文字、(d) <b>運用者が打った検証コードの echo</b> を含む。
/// (d) が効く —— <c>script</c> は tty の記録なので、<b>投入したコードがそのまま複製へ残る</b>。
/// 何もしなければ <c>GET /state</c> が<b>コードを画面へ返してしまう</b>ので、値は必ず伏せる。
/// </para>
/// </summary>
public static partial class ConsoleTail
{
    /// <summary>伏せ字。値の長さも漏らさないよう固定長にする。</summary>
    private const string Redacted = "***";

    /// <summary>ANSI CSI（<c>ESC [ … 終端</c>）。<c>script</c> の記録に必ず混ざる。</summary>
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiCsiPattern();

    /// <summary>ANSI OSC（<c>ESC ] … BEL</c> か <c>ESC ] … ESC \</c>）。端末タイトル設定などで出る。</summary>
    [GeneratedRegex(@"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiOscPattern();

    /// <summary>上記に当てはまらない裸の <c>ESC</c> ＋ 1 文字（2 バイトのエスケープ）。</summary>
    [GeneratedRegex(@"\x1B.", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex AnsiShortPattern();

    /// <summary>
    /// 資格情報を運ぶ引数の値。<c>-code=</c>（検証コード）と <c>-login_pwd=</c>（ログインパスワード）の
    /// 両方を伏せる。<b>キー名は残す</b> —— <c>Command Tips</c> 行の
    /// <c>input_phone_verify_code</c> 等はプロンプト判定に要るためである。
    /// </summary>
    [GeneratedRegex(@"(-(?:code|login_pwd)=)\S*", RegexOptions.CultureInvariant)]
    private static partial Regex SecretArgumentPattern();

    /// <summary>
    /// コンソールの生テキストを、画面へ出してよい形へ整える。
    /// ANSI エスケープ除去 → 改行正規化 → 制御文字除去 → 資格情報の伏せ字、の順に適用する。
    /// </summary>
    public static string Sanitize(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var text = AnsiCsiPattern().Replace(raw, string.Empty);
        text = AnsiOscPattern().Replace(text, string.Empty);
        text = AnsiShortPattern().Replace(text, string.Empty);

        // `script` の記録は CRLF になる。`\r` 単独は行内上書き（プログレス表示）なので改行として扱う。
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            // NUL を含む制御文字は落とす（改行とタブだけ残す）。
            if (ch is '\n' or '\t' || !char.IsControl(ch)) builder.Append(ch);
        }

        return SecretArgumentPattern().Replace(builder.ToString(), $"$1{Redacted}");
    }

    /// <summary>
    /// 整形済みのコンソール末尾から、<b>いま OpenD が待っているプロンプト</b>を読む。
    /// <para>
    /// 判定は <c>Command Tips</c> を含む<b>最後の行</b>だけを見る。コンソールには過去の
    /// <c>Command Tips</c> が何度も現れるため、<b>最後のもの以外は現在の状態を表さない</b>。
    /// 同じ行に複数のコマンドが並ぶ場合は「入力を求めるもの」を優先する
    /// （<c>pic</c> → <c>phone</c> → <c>resend</c>）。
    /// </para>
    /// </summary>
    /// <returns>待たれているプロンプト。判定できなければ <c>null</c>。</returns>
    public static VerifyKind? DetectPrompt(string sanitizedConsole)
    {
        if (string.IsNullOrEmpty(sanitizedConsole)) return null;

        var lines = sanitizedConsole.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (!line.Contains("Command Tips", StringComparison.OrdinalIgnoreCase)) continue;

            if (line.Contains("input_pic_verify_code", StringComparison.Ordinal)) return VerifyKind.Pic;
            if (line.Contains("input_phone_verify_code", StringComparison.Ordinal)) return VerifyKind.Phone;
            if (line.Contains("req_phone_verify_code", StringComparison.Ordinal)) return VerifyKind.Resend;

            // Command Tips ではあるが検証コード以外の案内である。過去の行まで遡らない
            // （遡ると「もう終わった検証」を現在のプロンプトとして返してしまう）。
            return null;
        }

        return null;
    }

    /// <summary>プロンプトを応答へ載せる文字列（クライアントの <c>kind</c> と同じ語彙）。</summary>
    public static string? ToWireValue(VerifyKind? prompt) => prompt switch
    {
        VerifyKind.Phone => "phone",
        VerifyKind.Pic => "pic",
        VerifyKind.Resend => "resend",
        _ => null,
    };
}
