using System.Text;

namespace AiStockTrading.Shared.Contracts.Logging;

// NFR, IADR-0316, #708（MSP#1015 の受け皿）: **行指向のログへ落とす前に、外部由来の文字列を発生源で正規化する**
// （CWE-117・log forging）。
//
// 脅威: LLM の生出力・外部 API の応答本文・Discord から届いた文言などは、改行・CR・ESC を含み得る。
// これらを構造化ログの引数へ素通しすると、1 レコードのはずのログが**複数行に割れ**、攻撃者が「偽の行」を
// 注入できる（例: 本文へ `正常\n2026-09-09 12:00:00 [INF] 取引ガードを解除しました` を仕込む）。
// ESC は端末で表示を書き換え、U+2028 / U+2029 は JSON 文字列としては合法なまま JavaScript の行区切りとして
// 働くため、ログビューアの側で行が割れる。
//
// 🔴 **既定オフ（IADR-0061 決定1）は代替にならない。** 障害調査で有効化した瞬間に露出する。
// **本ヘルパは既定オフに置き換わるものではなく、それに加える発生源の正規化である。**
//
// 方針は「無害化であって検閲ではない」（Discord 投稿の ReportSummarySanitizer と同じ思想）。
// 本文は読める形で残し、**行として成立しないようにするだけ**である —— 値をログから消してしまうと
// 「正規化した」と「そもそも書かなかった」が区別できず、調査のためのログという目的自体が失われる。
//
// 置き場: 外部ライブラリへ一切依存しない純関数であり、**消費側（サービス 3 本と共有 KB クライアント）すべての
// 推移閉包に既に入っている唯一の共有プロジェクト**が本プロジェクトである（IADR-0316 §理由）。
public static class LogSanitizer
{
    /// <summary>ログへ落とす 1 値あたりの既定の長さ上限（文字数）。</summary>
    public const int DefaultMaxLength = 4000;

    /// <summary>制御文字・行区切りの置換先。1 文字を 1 文字へ置換する（本文の長さは変えない）。</summary>
    public const char Replacement = '_';

    /// <summary>行区切りとして働く非制御文字（U+2028 LINE SEPARATOR）。</summary>
    public const char LineSeparator = '\u2028';

    /// <summary>行区切りとして働く非制御文字（U+2029 PARAGRAPH SEPARATOR）。</summary>
    public const char ParagraphSeparator = '\u2029';

    /// <summary>
    /// 外部由来の文字列を、1 行のログ値として安全な形へ正規化する。
    /// <para>
    /// (1) 制御文字（LF / CR / TAB / ESC / NUL などの C0・C1。U+0085 NEXT LINE は C1 のため
    /// <see cref="char.IsControl(char)"/> が拾う）と、行区切りとして働く U+2028 / U+2029 を
    /// <see cref="Replacement"/> へ置換する。
    /// (2) <paramref name="maxLength"/> で切り、切ったことと落とした文字数を末尾へ明示する。
    /// </para>
    /// null は null のまま返す（構造化ログ側の null 表現を壊さないため）。
    /// **多段に通さないこと** —— 切り詰めの注記まで再び切り詰めの対象になる。
    /// </summary>
    public static string? Sanitize(string? value, int maxLength = DefaultMaxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);

        if (value is null)
            return null;

        // サロゲートペアを割らない位置まで戻して切る（切断で不正な UTF-16 を作らない）。
        var kept = Math.Min(value.Length, maxLength);
        if (kept < value.Length && char.IsHighSurrogate(value[kept - 1]))
            kept--;

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < kept; i++)
        {
            var ch = value[i];
            sb.Append(IsLineBreaking(ch) ? Replacement : ch);
        }

        if (kept < value.Length)
            sb.Append($"…(truncated {value.Length - kept} chars)");

        return sb.ToString();
    }

    // 「行を割り得る文字」の判定。制御文字はレコード分割（LF / CR）と表示破壊（ESC）の両方を含む。
    private static bool IsLineBreaking(char ch) =>
        char.IsControl(ch) || ch is LineSeparator or ParagraphSeparator;
}
