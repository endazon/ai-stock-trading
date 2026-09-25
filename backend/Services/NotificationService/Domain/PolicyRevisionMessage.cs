using System.Text;

namespace NotificationService.Domain;

// FR-07, FR-14, UC-03〜05, ADR-0003, #1016, IADR-0431 決定 5: 方針の改訂案の Discord 表示文（純関数）。
//
// 🔴 **確定される方針は全文を見せる（切り詰めない）。** 利用者が確定するのはこの全文であり（ADR-0003「方針の確定には
// 利用者との対話を要する」）、見ていない部分を確定させない。Discord の 1 通の上限（2000 文字）に収まらなければ、
// 方針だけを複数の通に分けて順に送る（`【方針案 i/n】` の見出しつき）。確認ボタンは最後の通に付け、全文を送り終えた
// 後にしか出ない（ゲートウェイは通を順に送り、途中で送れなければ以降＝ボタンも送らない）。
//
// 切り詰めてよいのは**確定されない**表示だけ——監視銘柄の入れ替え案の理由（表示のみ）と AI の説明。まず理由を
// 短くし、次に説明を短くして、最後の通（入れ替え案＋説明＋ゲートウェイが足す確定の案内）を上限に収める。
// 入れ替え案の全文と指示の原文は報告書の本文（`GET /reports/{periodKey}`）に残る。
public static class PolicyRevisionMessage
{
    /// <summary>1 通の本文の上限。Discord の上限 2000 から、ゲートウェイが最後の通に足す確定の案内の余白を引いた値。</summary>
    public const int MaxLength = 1800;

    /// <summary>入れ替え案の理由を全件で収めきれないときに切り詰める長さ。</summary>
    public const int ShortReasonLength = 60;

    public const string WatchlistNotice = "（表示のみ。監視銘柄は変わっていません。適用する場合は設定画面から変更してください）";

    /// <summary>方針の分割の見出し（`【方針案 1/2】`）。本文はこの行の次から。</summary>
    public static string PolicyHeading(int index, int count) => $"【方針案 {index}/{count}】";

    /// <summary>
    /// 表示する通を順に返す。先頭は見出し、続いて方針（全文・必要なら分割）、最後は入れ替え案と説明。
    /// 確認ボタンは最後の通にだけ付ける。
    /// </summary>
    public static IReadOnlyList<string> Build(
        string periodKey,
        int version,
        bool presented,
        bool created,
        string policySummary,
        IReadOnlyList<(string Action, string Symbol, string Reason)> watchlistChanges,
        string? rationale)
    {
        ArgumentNullException.ThrowIfNull(policySummary);
        ArgumentNullException.ThrowIfNull(watchlistChanges);

        var messages = new List<string>();

        var header = new StringBuilder();
        header.Append($"方針の改訂案を作成しました（{periodKey}・版 {version}・{(presented ? "承認待ち" : "未提示")}）。");
        if (created)
            header.Append($"\n{periodKey} を新しく作りました。");
        header.Append("\n確定するまで取引には適用されません。方針案は全文を次に送ります。");
        messages.Add(header.ToString());

        // 方針（全文）。見出し＋改行を差し引いた長さで割る（サロゲートペアの途中では割らない）。
        var chunks = Split(policySummary, MaxLength - PolicyHeading(99, 99).Length - 1);
        for (var i = 0; i < chunks.Count; i++)
            messages.Add($"{PolicyHeading(i + 1, chunks.Count)}\n{chunks[i]}");

        messages.Add(Tail(watchlistChanges, rationale));
        return messages;
    }

    // 最後の通: 入れ替え案（理由は必要なら短く）と説明（必要なら短く）。
    private static string Tail(IReadOnlyList<(string Action, string Symbol, string Reason)> watchlistChanges, string? rationale)
    {
        string Watchlist(int? reasonLimit)
        {
            var sb = new StringBuilder("【監視銘柄の入れ替え案】").Append(WatchlistNotice);
            if (watchlistChanges.Count == 0)
                sb.Append("\n- なし");
            foreach (var (action, symbol, reason) in watchlistChanges)
                sb.Append($"\n- {ActionLabel(action)} {symbol}: {(reasonLimit is { } l ? Truncate(reason, l) : reason)}");
            return sb.ToString();
        }

        var watchlist = Watchlist(null);
        if (watchlist.Length > MaxLength)
            watchlist = Watchlist(ShortReasonLength);
        if (watchlist.Length > MaxLength)
            watchlist = Truncate(watchlist, MaxLength);

        if (string.IsNullOrEmpty(rationale))
            return watchlist;

        const string rationaleHeading = "\n\n【AI の説明】\n";
        var budget = MaxLength - watchlist.Length - rationaleHeading.Length;
        return budget <= 1 ? watchlist : watchlist + rationaleHeading + Truncate(rationale, budget);
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

    private static string ActionLabel(string action) => action switch
    {
        "add" => "追加",
        "remove" => "除外",
        _ => action,
    };

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : max <= 1 ? string.Empty : text[..(max - 1)] + "…";
}
