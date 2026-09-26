namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;

// FR-01, FR-03, ADR-0043（計画）決定 1, #1030, IADR-0437: Finnhub の 429 を「分次の窓で説明できるか」で分ける純関数。
//
// 分次の上限は 60 回 / 60 秒の固定ウィンドウで、`X-Ratelimit-Remaining` は要求ごとに 1 ずつ減り 0 で 429、
// `X-Ratelimit-Reset` の時刻を過ぎると満額へ戻る（IADR-0275 の実測）。したがって次の 429 は分次の窓では説明できない
// ——日次上限（公式に記載が無く、未実測）の手がかりとして区別して記録する（ADR-0043 決定 1「日次は実際の 429 で見張る」）。
//   - 残りがあるのに拒否された（`X-Ratelimit-Remaining` > 0）
//   - リセットの時刻を過ぎても拒否が続く（その応答のリセットが既に過去／その応答にリセットが無く、前回の 429 のリセットを過ぎた後、
//     成功を挟まずにまた 429）
// 残りがあっても直前の要求から 1 秒以内の 429 は秒次（30 回/秒）の上限で説明できる余地があるため手がかりにしない（#1037 の監査）。
// 時計のずれで誤って鳴らさないよう、リセットの判定には猶予（<see cref="ResetGrace"/>）を置く。
public static class FinnhubRateLimitClassifier
{
    public const string RemainingHeader = "X-Ratelimit-Remaining";
    public const string ResetHeader = "X-Ratelimit-Reset";

    /// <summary>リセットの時刻を「過ぎた」と読むまでの猶予（Finnhub の時計と手元の時計のずれ・秒の丸めを吸収する）。</summary>
    public static readonly TimeSpan ResetGrace = TimeSpan.FromSeconds(2);

    public enum Kind
    {
        /// <summary>分次の窓で説明できる（残り 0・リセットは未来）。</summary>
        MinuteWindow,

        /// <summary>残りがあるのに拒否された。分次では説明できない。</summary>
        RemainingNotExhausted,

        /// <summary>リセットの時刻を過ぎても拒否が続く。分次では説明できない。</summary>
        PersistsAfterReset,

        /// <summary>残り・リセットのヘッダがどちらも無く、判別できない。</summary>
        HeadersMissing,

        /// <summary>残りがあるのに拒否されたが、直前の要求から 1 秒以内（秒次 30 回/秒の上限で説明できる余地がある）。</summary>
        PossibleBurst,
    }

    /// <summary>
    /// 秒次の上限（Finnhub は全プランの上限に加えて 30 回/秒）の 429 かもしれないと読む間隔。直前の要求から 1 秒以内の
    /// 「残りがあるのに拒否」は秒次で説明できる余地があるため、日次の手がかりにしない。
    /// </summary>
    public static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 429 を分類する。<paramref name="previousRejectionReset"/> は、成功を挟まずに直前に受けた 429 のリセット時刻（無ければ null）。
    /// <paramref name="sincePreviousRequest"/> は、このクライアントが直前に要求を送ってからこの要求を送るまでの間隔（初回は null）。
    /// </summary>
    /// <remarks>
    /// #1037 の監査で改めた 2 点:
    /// <list type="bullet">
    /// <item><b>前回の 429 のリセットで判定するのは、この応答にリセットが無いときだけ。</b> この応答が「残り 0・リセットは未来」なら、
    /// 新しい窓を使い切った 429 として分次で説明できる（前回の窓のリセットを過ぎていても手がかりにしない）。</item>
    /// <item><b>秒次（30 回/秒）の 429 は残りがあっても出る。</b> 直前の要求から <see cref="BurstWindow"/> 以内の「残りがあるのに拒否」は
    /// <see cref="Kind.PossibleBurst"/> とし、手がかりにしない。🔴 見えるのはこのプロセスの要求だけで、同じ鍵を使う他のプロセスの
    /// 同時の送出は見えない（それによる秒次の 429 は手がかりとして記録され得る。記録を読む側が他のプロセスの時刻と突き合わせる）。</item>
    /// </list>
    /// </remarks>
    public static Kind Classify(
        int? remaining,
        DateTimeOffset? reset,
        DateTimeOffset? previousRejectionReset,
        DateTimeOffset now,
        TimeSpan? sincePreviousRequest = null)
    {
        if (remaining is > 0)
            return sincePreviousRequest is { } gap && gap < BurstWindow ? Kind.PossibleBurst : Kind.RemainingNotExhausted;
        if (reset is { } r)
            return r + ResetGrace <= now ? Kind.PersistsAfterReset : Kind.MinuteWindow;
        if (previousRejectionReset is { } p && p + ResetGrace <= now)
            return Kind.PersistsAfterReset;
        return remaining is null ? Kind.HeadersMissing : Kind.MinuteWindow;
    }

    /// <summary>日次上限の手がかりとして記録すべきか（分次の窓では説明できない 429）。</summary>
    public static bool IsDailyLimitClue(Kind kind) => kind is Kind.RemainingNotExhausted or Kind.PersistsAfterReset;
}
