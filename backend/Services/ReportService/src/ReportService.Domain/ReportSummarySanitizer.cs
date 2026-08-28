using System.Text;

namespace ReportService.Domain;

// FR-09, ADR-0003, IADR-0116 決定3, #280: Discord へ投稿する要約本文の無害化（純関数・冪等）。
//
// 本文には LLM 生成の散文が入る。防御思想は情報収集の PromptSafetySanitizer（IADR-0022）と同じ
// 「データを命令にしない・制御文字を落とす・境界を偽装させない」だが、**あちらは本文を
// <<<UNTRUSTED_DATA … UNTRUSTED_DATA>>> で囲う関数**であり、人間が読む投稿に境界語を被せるのは誤りである。
// そのため型は共有せず、投稿先の脅威モデル（メンションの一斉通知・表示破壊）に合わせて実装する。
//
// 方針は「検閲ではなく無害化」。本文は読める形で残し、構文としてだけ成立しないようにする。
public static class ReportSummarySanitizer
{
    /// <summary>要約全体の既定の長さ上限（文字数）。Discord の投稿長と監査の可読性に合わせる。</summary>
    public const int DefaultMaxLength = 1200;

    /// <summary>メンション構文を壊すために挿入する幅ゼロ空白（U+200B）。表示は変えずに構文だけ無効化する。</summary>
    public static readonly string MentionBreaker = ((char)0x200B).ToString();

    // IADR-0022 の PromptSafetySanitizer と同一の境界語。型は共有しない（理由は IADR-0116 決定3）ため、
    // 値の重複を許容する。プロンプト仕様を変える際は本 IADR とあわせて見直すこと。
    private const string UntrustedOpenDelimiter = "<<<UNTRUSTED_DATA";
    private const string UntrustedCloseDelimiter = "UNTRUSTED_DATA>>>";

    /// <summary>投稿に載せられる形へ無害化する。null・空白のみは空文字。</summary>
    public static string Sanitize(string? text, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // 1. 制御文字を落とす（改行だけ残す）。CR は落ちるため CRLF は LF になる。
        //    タブも落とす（Discord・ログでの桁揃えが崩れるだけで情報を持たない）。
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsControl(ch) && ch != '\n')
                continue;
            sb.Append(ch);
        }
        var result = sb.ToString();

        // 2. 収集情報の境界語を落とす（本文が境界を偽装できないようにする・多層防御）。
        result = result
            .Replace(UntrustedOpenDelimiter, string.Empty, StringComparison.Ordinal)
            .Replace(UntrustedCloseDelimiter, string.Empty, StringComparison.Ordinal);

        // 3. Discord のメンション構文を壊す。@everyone/@here は一斉通知、<@ <@! <@& <# は個別メンション。
        //    幅ゼロ空白を差し込むだけなので、読み手には同じ文字列に見える。冪等（挿入後は再び一致しない）。
        result = result
            .Replace("@everyone", $"@{MentionBreaker}everyone", StringComparison.Ordinal)
            .Replace("@here", $"@{MentionBreaker}here", StringComparison.Ordinal)
            .Replace("<@", $"<{MentionBreaker}@", StringComparison.Ordinal)
            .Replace("<#", $"<{MentionBreaker}#", StringComparison.Ordinal);

        // 4. 空行の連続を畳む（境界語の除去で生じた空白も含めて整える）。
        result = CollapseBlankLines(result).Trim();

        // 5. 長さ上限（末尾を省略記号にする。上限そのものを超えない）。
        return result.Length <= maxLength
            ? result
            : result[..Math.Max(0, maxLength - 1)] + "…";
    }

    // 3 行以上の連続改行を 2 行（＝空行 1 つ）へ畳む。
    private static string CollapseBlankLines(string text)
    {
        var sb = new StringBuilder(text.Length);
        var newlineRun = 0;

        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                newlineRun++;
                if (newlineRun > 2)
                    continue;
            }
            else
            {
                newlineRun = 0;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }
}
