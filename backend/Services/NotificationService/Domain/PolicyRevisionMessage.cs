using System.Text;

namespace NotificationService.Domain;

// FR-07, FR-14, UC-03〜05, ADR-0003, #1016, IADR-0431 決定 5: 方針の改訂案の Discord 表示文（純関数）。
//
// 🔴 **確定される方針は全文を見せる（切り詰めない）。** 利用者が確定するのはこの全文であり（ADR-0003「方針の確定には
// 利用者との対話を要する」）、見ていない部分を確定させない。Discord の 1 通の上限（2000 文字）に収まらなければ、
// 方針だけを複数の通に分けて順に送る（`【方針案 i/n】` の見出しつき）。確認ボタンは最後の通に付け、全文を送り終えた
// 後にしか出ない（ゲートウェイは通を順に送り、途中で送れなければ以降＝ボタンも送らない）。
//
// FR-13, ADR-0042 決定 1, #1025, IADR-0433 決定 5: 🔴 **確定で適用される監視銘柄の入れ替えも、銘柄と理由を全文で見せる**
// （「確認ボタンの前に、追加・除外する銘柄とその理由を表示する」）。収まらなければ入れ替えの行を複数の通に分ける。
// 切り詰めてよいのは確定・適用されない表示——AI の説明——だけである。
public static class PolicyRevisionMessage
{
    /// <summary>1 通の本文の上限。Discord の上限 2000 から、ゲートウェイが最後の通に足す確定の案内の余白を引いた値。</summary>
    public const int MaxLength = 1800;

    /// <summary>方針の 1 通の本文の最大長（見出しの行を差し引いた値）。</summary>
    public static readonly int PolicyChunkSize = MaxLength - PolicyHeading(99, 99).Length - 1;

    public const string WatchlistHeading = "【監視銘柄の入れ替え案】";

    /// <summary>確定すると入れ替えも適用される案の注記。</summary>
    public const string WatchlistWillApplyNotice =
        "（下の確認ボタンで確定すると、案を作った時点から監視銘柄が変わっていなければ、この入れ替えも適用します。"
        + "/report approve で確定したときは適用しません）";

    /// <summary>案を作った時点の監視銘柄が分からない案の注記（適用しない）。</summary>
    public const string WatchlistUnknownNotice =
        "（現在の監視銘柄を照会できなかったため、確定しても入れ替えは適用しません。適用する場合は設定画面から変更してください）";

    /// <summary>方針の分割の見出し（`【方針案 1/2】`）。本文はこの行の次から。</summary>
    public static string PolicyHeading(int index, int count) => $"【方針案 {index}/{count}】";

    /// <summary>
    /// 表示する通を順に返す。先頭は見出し、続いて方針（全文・必要なら分割）、入れ替え案（全文・必要なら分割）、最後に説明。
    /// 確認ボタンは最後の通にだけ付ける。
    /// </summary>
    public static IReadOnlyList<string> Build(
        string periodKey,
        int version,
        bool presented,
        bool created,
        string serviceMessage,
        string policySummary,
        IReadOnlyList<(string Action, string Symbol, string Reason)> watchlistChanges,
        string? rationale,
        bool watchlistSnapshotKnown = true)
    {
        ArgumentNullException.ThrowIfNull(policySummary);
        ArgumentNullException.ThrowIfNull(watchlistChanges);

        var messages = new List<string>();

        var header = new StringBuilder();
        header.Append($"方針の改訂案を作成しました（{periodKey}・版 {version}・{(presented ? "承認待ち" : "未提示")}）。");
        if (created)
            header.Append($"\n{periodKey} を新しく作りました。");
        // 承認待ちにできなかったときは、報告書サービスの案内（何が保存され、どう確かめるか）をそのまま見せる。
        if (!presented && !string.IsNullOrWhiteSpace(serviceMessage))
            header.Append('\n').Append(serviceMessage);
        header.Append("\n確定するまで取引には適用されません。方針案は全文を次に送ります。");
        messages.Add(header.ToString());

        // 方針（全文）。見出し＋改行を差し引いた長さで割る（サロゲートペアの途中では割らない）。
        var chunks = Split(policySummary, PolicyChunkSize);
        for (var i = 0; i < chunks.Count; i++)
            messages.Add($"{PolicyHeading(i + 1, chunks.Count)}\n{chunks[i]}");

        // 入れ替え案（全文）。行（1 件＝最大 200 文字の理由）の途中では割らない。
        var notice = watchlistChanges.Count == 0 ? string.Empty
            : watchlistSnapshotKnown ? WatchlistWillApplyNotice : WatchlistUnknownNotice;
        var lines = watchlistChanges.Count == 0
            ? ["- なし"]
            : watchlistChanges.Select(c => $"- {ActionLabel(c.Action)} {c.Symbol}（米国）: {c.Reason}").ToList();
        var current = new StringBuilder(WatchlistHeading).Append(notice);
        var watchlistMessages = new List<string>();
        foreach (var line in lines)
        {
            if (current.Length + 1 + line.Length > MaxLength)
            {
                watchlistMessages.Add(current.ToString());
                current = new StringBuilder(WatchlistHeading).Append("（続き）");
            }

            current.Append('\n').Append(line);
        }

        watchlistMessages.Add(current.ToString());

        // 説明（確定・適用されない表示。上限に収まらなければ切り詰める）。最後の入れ替えの通に収まればそこへ、無理なら別の通。
        if (!string.IsNullOrEmpty(rationale))
        {
            const string rationaleHeading = "\n\n【AI の説明】\n";
            var last = watchlistMessages[^1];
            var budget = MaxLength - last.Length - rationaleHeading.Length;
            if (budget >= Math.Min(rationale.Length, 200))
                watchlistMessages[^1] = last + rationaleHeading + Truncate(rationale, budget);
            else
                watchlistMessages.Add("【AI の説明】\n" + Truncate(rationale, MaxLength - "【AI の説明】\n".Length));
        }

        messages.AddRange(watchlistMessages);
        return messages;
    }

    // 上限の長さごとに割る。割り目がサロゲートペアの上位にかかるなら 1 文字手前で割る（文字を壊さない）。
    internal static IReadOnlyList<string> Split(string text, int size)
    {
        if (text.Length == 0)
            return [string.Empty];

        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(size, text.Length - start);
            if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]))
                length--;
            chunks.Add(text.Substring(start, length));
            start += length;
        }

        return chunks;
    }

    public static string ActionLabel(string action) => action switch
    {
        "add" => "追加",
        "remove" => "除外",
        _ => action,
    };

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : max <= 1 ? string.Empty : text[..(max - 1)] + "…";
}
