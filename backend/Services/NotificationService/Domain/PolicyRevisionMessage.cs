using System.Text;

namespace NotificationService.Domain;

// FR-07, FR-14, UC-03〜05, #1016, IADR-0431 決定 5: 方針の改訂案の Discord 表示文（純関数）。
//
// Discord のメッセージ長の上限（2000 文字）に収める。方針・説明は長さの予算を超えたら末尾を省略する
// （全文は報告書の本文に残る。`/report show` と閲覧で確認できる）。
// 監視銘柄の入れ替え案は**表示のみ**であることを必ず添える（FR-14: Discord から設定値は変えない）。
public static class PolicyRevisionMessage
{
    // Discord の上限 2000 から、確認ボタンの前置き文などの余白を引いた本文の上限。
    public const int MaxLength = 1800;

    public const string WatchlistNotice = "（表示のみ。監視銘柄は変わっていません。適用する場合は設定画面から変更してください）";

    public static string Format(
        string periodKey,
        int version,
        bool presented,
        bool created,
        bool autoGenerationSkipped,
        string policySummary,
        IReadOnlyList<(string Action, string Symbol, string Reason)> watchlistChanges,
        string? rationale)
    {
        ArgumentNullException.ThrowIfNull(watchlistChanges);

        var header = new StringBuilder();
        header.Append($"方針の改訂案を作成しました（{periodKey}・版 {version}・{(presented ? "承認待ち" : "未提示")}）。");
        if (created)
            header.Append($"\n{periodKey} を新しく作りました。");
        if (autoGenerationSkipped)
            header.Append("\n⚠️ この日の日報は自動生成されません（既存の報告書を上書きしない規則のため）。");
        header.Append("\n確定するまで取引には適用されません。");

        var watchlist = new StringBuilder();
        watchlist.Append("\n\n【監視銘柄の入れ替え案】").Append(WatchlistNotice);
        if (watchlistChanges.Count == 0)
        {
            watchlist.Append("\n- なし");
        }
        else
        {
            foreach (var (action, symbol, reason) in watchlistChanges)
                watchlist.Append($"\n- {ActionLabel(action)} {symbol}: {reason}");
        }

        // 方針・説明に割ける長さ（固定部分を先に確保し、方針を優先する）。
        var fixedLength = header.Length + watchlist.Length + "\n\n【方針案】\n".Length + "\n\n【AI の説明】\n".Length;
        var budget = Math.Max(0, MaxLength - fixedLength);
        var policyText = Truncate(policySummary, budget);
        var rationaleText = rationale is null ? null : Truncate(rationale, Math.Max(0, budget - policyText.Length));

        var sb = new StringBuilder();
        sb.Append(header);
        sb.Append("\n\n【方針案】\n").Append(policyText);
        sb.Append(watchlist);
        if (!string.IsNullOrEmpty(rationaleText))
            sb.Append("\n\n【AI の説明】\n").Append(rationaleText);

        var text = sb.ToString();
        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }

    private static string ActionLabel(string action) => action switch
    {
        "add" => "追加",
        "remove" => "除外",
        _ => action,
    };

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : max <= 1 ? string.Empty : text[..(max - 1)] + "…";
}
