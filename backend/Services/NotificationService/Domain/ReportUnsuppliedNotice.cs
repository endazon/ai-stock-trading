using System.Text;

namespace NotificationService.Domain;

// FR-07, FR-14, UC-03〜05, #840, IADR-0352 決定 5: `/report show`（と版番号なしの `/report approve`＝確認ボタンの前段）へ
// 併記する「入力が未供給のまま生成された報告書である」旨の警告文（純関数）。
//
// 報告書サービスのレビュー照会（GET /reports/{periodKey}/review）が返す `unsuppliedInputs` は、同サービスの
// **コード定数の表示名**であり、報告書の本文・要約（LLM 出力）ではない。したがって IADR-0240 決定4
// （Bot は本文・要約を取りに行かない＝サニタイズ済みの通知経路を迂回しない）には反しない。
//
// 🔴 それでも**別プロセスから届いた文字列**であり、そのまま Discord へ出さない（多層防御）:
//   - 制御文字（改行を含む）を落とす —— 1 項目が複数行を装って別の文言を差し込めないようにする。
//   - メンション構文を壊す（@everyone / @here / <@ / <#）。
//   - 1 項目の長さと項目数に上限を置く（応答が Discord の投稿長を超えない）。
public static class ReportUnsuppliedNotice
{
    /// <summary>警告文の先頭。報告書サービスの提示通知（ReportSummary）と同じ語にして、利用者が同じ事象と読めるようにする。</summary>
    public const string Prefix = "⚠ 未供給の入力があります";

    public const int MaxItems = 16;

    public const int MaxItemLength = 40;

    // 幅ゼロ空白（U+200B）。表示を変えずにメンション構文だけを無効化する（報告書サービスの要約サニタイザと同じ手当て）。
    private static readonly string MentionBreaker = ((char)0x200B).ToString();

    /// <summary>警告文（先頭に改行を含まない 1 行）。未供給が無ければ <c>null</c>。</summary>
    public static string? Format(IEnumerable<string?>? unsuppliedInputs)
    {
        if (unsuppliedInputs is null)
            return null;

        var items = unsuppliedInputs
            .Select(Clean)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (items.Count == 0)
            return null;

        var shown = items.Take(MaxItems).ToList();
        var rest = items.Count - shown.Count;
        var list = string.Join("、", shown) + (rest > 0 ? $" ほか {rest} 件" : string.Empty);

        return $"{Prefix}（確定の前に本文を確認してください）: {list}";
    }

    private static string Clean(string? item)
    {
        if (string.IsNullOrWhiteSpace(item))
            return string.Empty;

        var sb = new StringBuilder(item.Length);
        foreach (var ch in item)
        {
            if (!char.IsControl(ch))
                sb.Append(ch);
        }

        var cleaned = sb.ToString().Trim()
            .Replace("@everyone", $"@{MentionBreaker}everyone", StringComparison.Ordinal)
            .Replace("@here", $"@{MentionBreaker}here", StringComparison.Ordinal)
            .Replace("<@", $"<{MentionBreaker}@", StringComparison.Ordinal)
            .Replace("<#", $"<{MentionBreaker}#", StringComparison.Ordinal);

        return cleaned.Length <= MaxItemLength ? cleaned : cleaned[..MaxItemLength] + "…";
    }
}
