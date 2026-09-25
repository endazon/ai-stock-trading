namespace NotificationService.Domain;

// FR-07, FR-14, UC-03〜05, #834, IADR-0240: `/report` の period（会話キー）入力補完の候補を選ぶ純関数。
//
// 利用者は会話キー（`daily-2026-09-18` / `weekly-2026-W38` / `monthly-2026-09`）を覚えていられず、
// 日付だけを入れて 404 になった（#834 で実測。4 回連続）。候補を出せば誤入力そのものが減る。
//
// 🔴 **推測補正はしない**（#835 決定3 と同じ方針）。利用者が打った文字で絞るだけで、キーを直したり
// 補ったりしない。**候補に出すのは `BotCommandParser` が受け付ける値域のものだけ**である
// ——受け付けない値を候補に出すと、選んだ結果が `Unknown` へ倒れて何も起きない。
//
// 判断（絞り込み・並び・上限）を Gateway ライブラリ（Discord 連携のクライアント実装）に依存しない純関数へ
// 置き、Gateway 側は変換に徹する（IADR-0062 決定3 / IADR-0128 決定6 と同じ分け方。**Domain は外部ライブラリの
// 名前空間を完全修飾でも書かない**——`DomainSourceDependencyTests` の検査 (c) が本文を走査して止める）。
public static class ReportPeriodSuggestions
{
    /// <summary>Discord の入力補完が 1 回に返せる候補数の上限（Discord API の制約）。</summary>
    // #843 項目6（受容）: 上限は**状態で絞らない新しい順**に掛かるため、確定済みも候補に入る（IADR-0240 決定5・
    // 決定9 の意図的な選択）。空入力時にレビュー待ちより新しい報告書が 25 件以上あると押し出されるが、運用上
    // レビュー待ちは最新であり実害は想定しにくい。軽い一覧（#843 項目1・IADR-0418）の実装時に再考し、受容を
    // 据え置いた（状態を載せて並びを変えるのは補完の見え方の変更で、押し出しは観測されていない）。
    // 再考の条件: 押し出しの報告、または報告書の件数が数百件に近づいたとき。
    public const int MaxChoices = 25;

    /// <summary>
    /// 一覧（新しい順に並んだ会話キー）を利用者の入力で絞り込み、上限まで返す。
    /// 前方一致を部分一致より前に置く（大小文字は無視）。入力が空なら新しい順のまま返す。
    /// 並びは**入力元の順序を保つ**（新しい順は呼び出し側＝報告書サービスの一覧が決める）。
    /// </summary>
    public static IReadOnlyList<string> Filter(IEnumerable<string> periodKeys, string? input)
    {
        ArgumentNullException.ThrowIfNull(periodKeys);

        // 値域外・重複を落とす（重複は候補として無意味であり、上限 25 の枠を食う）。
        var candidates = periodKeys
            .Where(BotCommandParser.IsPeriodKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var term = input?.Trim() ?? string.Empty;
        if (term.Length == 0)
            return [.. candidates.Take(MaxChoices)];

        var prefix = candidates
            .Where(k => k.StartsWith(term, StringComparison.OrdinalIgnoreCase));

        var contains = candidates
            .Where(k => !k.StartsWith(term, StringComparison.OrdinalIgnoreCase)
                && k.Contains(term, StringComparison.OrdinalIgnoreCase));

        return [.. prefix.Concat(contains).Take(MaxChoices)];
    }
}
