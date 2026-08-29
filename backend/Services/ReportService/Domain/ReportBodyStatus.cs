using System.Globalization;

namespace ReportService.Domain;

// FR-06, FR-07, #338, #310, INDEX 決定29, IADR-0252:
// **確定した報告書の本文が「未確定」を名乗り続けないようにする**（純関数・冪等）。
//
// 🔴 本文（Markdown）は**ドラフト生成時に一度だけ組み立てられ、確定では作り直されない**。
// そのため frontmatter の `status:` と機械可読な YAML ブロックの `status:` は、
// 確定後も `draft` のまま残る。**確定済みの報告書が自分を「未確定」と名乗る**状態であり、
// #310（確定後も「未確定」を名乗る本文が取引判断へ渡っていた）と同じ形である。
//
// 決定29 は「**取引判断サービスは YAML ブロックのみを読む**」と定めた。読む側が状態を YAML から
// 判断する以上、確定した事実が YAML に反映されないことは、**方針が採用されない／ドラフトが採用される**
// のいずれかの誤りへ直結する。
//
// 🔴 **本文を作り直さない。** 作り直すと散文（LLM 出力）が確定後に変わり得る——
// 確定とは「その本文でよい」と利用者が承認した行為であるため、**状態の行だけを書き換える**。
public static class ReportBodyStatus
{
    /// <summary>
    /// 本文中の状態表記を「確定済み」へ書き換える（<b>状態の行だけ</b>・冪等）。
    /// <para>
    /// 対象は <c>status: draft</c> の行（frontmatter と YAML ブロックの両方）と、
    /// <c>confirmed_at:</c> の行である。それ以外の行は 1 バイトも変えない。
    /// </para>
    /// </summary>
    /// <remarks>
    /// 🔴 <c>null</c> は <c>null</c> のまま返す。空文字へ倒すと「本文が無い（未生成）」と
    /// 「本文が空だった」の区別が消える——本サービスが一貫して守っている規律である。
    /// </remarks>
    public static string? MarkConfirmed(string? body, DateTimeOffset confirmedAt)
    {
        if (string.IsNullOrEmpty(body))
            return body;

        var normalized = body.ReplaceLineEndings("\n");
        var lines = normalized.Split('\n');
        var timestamp = confirmedAt.ToString("o", CultureInfo.InvariantCulture);

        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i] == "status: draft")
                lines[i] = "status: fixed";
            else if (lines[i].StartsWith("confirmed_at:", StringComparison.Ordinal))
                lines[i] = $"confirmed_at: {timestamp}";
        }

        return string.Join('\n', lines);
    }
}
