namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;

// FR-01, ADR-0031（計画）決定2〜4, IADR-0292: Finnhub の**日次総量**を見積もる純関数。
//
// ADR-0031 決定2（🔴 撤回しない統制）: 分次の自制レート（トークンバケット）は瞬間的な要求レートしか
// 保証せず、**一定期間の総量は守らない**。1 日の要求数は概ね
//   監視銘柄数 × 1 巡回あたりの要求数 × 1 日の巡回回数
// で決まり、**銘柄数に正比例する**。IADR-0275 決定5 が「監視銘柄数の絶対的な上限は不要」と結論づけた
// 論拠（トークンバケットは銘柄数に依らず一定レートを保証する）は**分次についてのみ正しい**（ADR-0031 決定2）。
//
// ADR-0031 決定3: 日次上限はまだ実測できていないため、**暫定手段として第三者観測の「約 300 回/日」を
// 計画上の前提値**として扱う（<see cref="FinnhubDailyVolumeGuardOptions.ProvisionalDailyLimit"/> の既定値）。
// 推測値を実測として焼き込まない（IADR-0224 の原則）ため、超過は**警告に留め、送出を止めない**
// （統制の実現手段としては「見積もりを可視化する」段階であり、確定した数値上限による強制ではない）。
//
// ADR-0031 決定4: 同一鍵を共有する全プロセスの見積りは合算する。<see cref="ApiKeyGroup"/> が
// 同一のプロセスだけを合算し、鍵が別のプロセスは独立に判定する（合算しない）。
public static class FinnhubDailyVolumeEstimator
{
    public enum Verdict
    {
        Within,
        Exceeds,
    }

    /// <summary>1 プロセスぶんの日次要求見積りの入力。</summary>
    /// <param name="ProcessName">プロセス（サービス）名。ログ・自己申告での表示用。</param>
    /// <param name="ApiKeyGroup">
    /// 同一 Finnhub 鍵を共有するプロセスを束ねる識別子（例: API キーの SHA-256 ハッシュ）。
    /// 生の鍵値は持たせない——ADR-0031 決定4 の実測（IADR-0275）と同じく、値を露出せずに同一性だけを比較する。
    /// </param>
    /// <param name="SymbolCount">1 巡回で問い合わせる銘柄数。</param>
    /// <param name="RequestsPerSymbolPerCycle">1 巡回・1 銘柄あたりの要求数。</param>
    /// <param name="CyclesPerDay">1 日あたりの巡回回数（<see cref="CyclesPerDay(int)"/> 参照）。</param>
    public readonly record struct ProcessVolume(
        string ProcessName,
        string ApiKeyGroup,
        int SymbolCount,
        int RequestsPerSymbolPerCycle,
        int CyclesPerDay)
    {
        public long EstimatedDailyRequests
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(SymbolCount);
                ArgumentOutOfRangeException.ThrowIfNegative(RequestsPerSymbolPerCycle);
                ArgumentOutOfRangeException.ThrowIfNegative(CyclesPerDay);
                return (long)SymbolCount * RequestsPerSymbolPerCycle * CyclesPerDay;
            }
        }
    }

    /// <summary>同一鍵（<see cref="ProcessVolume.ApiKeyGroup"/>）グループ 1 つぶんの合算結果。</summary>
    public readonly record struct KeyGroupEstimate(
        string ApiKeyGroup,
        long EstimatedDailyRequests,
        int ProvisionalDailyLimit,
        Verdict Verdict,
        double ExceedRatio,
        IReadOnlyList<string> ProcessNames);

    /// <summary>
    /// 巡回間隔（秒）から 1 日あたりの巡回回数を算出する（切り捨て）。取引時間帯に限る補正は行わない——
    /// 各サービスの既存の巡回実装（休場中スキップの有無）は呼び出し側の責務であり、本関数は
    /// 「間隔どおりに回り続けた場合の理論上限」という保守的な上振れ見積りを返す。
    /// </summary>
    public static int CyclesPerDay(int pollIntervalSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pollIntervalSeconds);
        const int SecondsPerDay = 24 * 60 * 60;
        return SecondsPerDay / pollIntervalSeconds;
    }

    /// <summary>単一プロセスの見積りを暫定日次上限と突き合わせる。</summary>
    public static KeyGroupEstimate Evaluate(ProcessVolume process, int provisionalDailyLimit) =>
        Evaluate([process], provisionalDailyLimit)[0];

    /// <summary>
    /// 既に算出済みの日次要求見積り（合計）を暫定日次上限と直接突き合わせる（銘柄数等の内訳を要さない場合の簡易版）。
    /// </summary>
    public static KeyGroupEstimate Evaluate(long estimatedDailyRequests, int provisionalDailyLimit, string processName = "")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedDailyRequests);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(provisionalDailyLimit);

        var verdict = estimatedDailyRequests > provisionalDailyLimit ? Verdict.Exceeds : Verdict.Within;
        var ratio = (double)estimatedDailyRequests / provisionalDailyLimit;
        return new KeyGroupEstimate(
            ApiKeyGroup: "self",
            EstimatedDailyRequests: estimatedDailyRequests,
            ProvisionalDailyLimit: provisionalDailyLimit,
            Verdict: verdict,
            ExceedRatio: ratio,
            ProcessNames: [processName]);
    }

    /// <summary>
    /// 複数プロセスの見積りを <see cref="ProcessVolume.ApiKeyGroup"/> でグルーピングして合算し、
    /// グループごとに暫定日次上限と突き合わせる。<b>鍵が別（ApiKeyGroup が異なる）プロセスは合算しない。</b>
    /// </summary>
    public static IReadOnlyList<KeyGroupEstimate> Evaluate(
        IReadOnlyCollection<ProcessVolume> processes, int provisionalDailyLimit)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(provisionalDailyLimit);
        if (processes.Count == 0)
            return [];

        return processes
            .GroupBy(p => p.ApiKeyGroup, StringComparer.Ordinal)
            .Select(group =>
            {
                var total = group.Sum(p => p.EstimatedDailyRequests);
                var verdict = total > provisionalDailyLimit ? Verdict.Exceeds : Verdict.Within;
                var ratio = (double)total / provisionalDailyLimit;
                return new KeyGroupEstimate(
                    group.Key, total, provisionalDailyLimit, verdict, ratio, [.. group.Select(p => p.ProcessName)]);
            })
            .ToArray();
    }
}
