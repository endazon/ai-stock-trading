namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;

// FR-01, FR-03, ADR-0043（計画）決定 1, #1030, IADR-0437: Finnhub の 429 を「分次の窓で説明できるか」で分ける純関数。
//
// 分次の上限は 60 回 / 60 秒の固定ウィンドウで、`X-Ratelimit-Remaining` は要求ごとに 1 ずつ減り 0 で 429、
// `X-Ratelimit-Reset` の時刻を過ぎると満額へ戻る（IADR-0275 の実測）。したがって次の 429 は分次の窓では説明できない
// ——日次上限（公式に記載が無く、未実測）の手がかりとして区別して記録する（ADR-0043 決定 1「日次は実際の 429 で見張る」）。
//   - 残りがあるのに拒否された（`X-Ratelimit-Remaining` > 0）
//   - リセットの時刻を過ぎても拒否が続く（その応答のリセットが既に過去／前回の 429 のリセットを過ぎた後、成功を挟まずにまた 429）
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
    }

    /// <summary>
    /// 429 を分類する。<paramref name="previousRejectionReset"/> は、成功を挟まずに直前に受けた 429 のリセット時刻（無ければ null）。
    /// </summary>
    public static Kind Classify(int? remaining, DateTimeOffset? reset, DateTimeOffset? previousRejectionReset, DateTimeOffset now)
    {
        if (remaining is > 0)
            return Kind.RemainingNotExhausted;
        if (reset is { } r && r + ResetGrace <= now)
            return Kind.PersistsAfterReset;
        if (previousRejectionReset is { } p && p + ResetGrace <= now)
            return Kind.PersistsAfterReset;
        if (remaining is null && reset is null)
            return Kind.HeadersMissing;
        return Kind.MinuteWindow;
    }

    /// <summary>日次上限の手がかりとして記録すべきか（分次の窓では説明できない 429）。</summary>
    public static bool IsDailyLimitClue(Kind kind) => kind is Kind.RemainingNotExhausted or Kind.PersistsAfterReset;
}
