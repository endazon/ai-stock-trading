namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;

// FR-01, ADR-0031（計画）決定2〜4, IADR-0292: Finnhub の**日次総量**を見積もる純関数。
//
// ADR-0031 決定2（🔴 撤回しない統制）: 分次の自制レート（トークンバケット）は瞬間的な要求レートしか
// 保証せず、**一定期間の総量は守らない**。1 日の要求数は概ね
//   監視銘柄数 × 1 巡回あたりの要求数 × 1 日の巡回回数
// で決まり、**銘柄数に正比例する**。IADR-0275 決定5 が「監視銘柄数の絶対的な上限は不要」と結論づけた
// 論拠（トークンバケットは銘柄数に依らず一定レートを保証する）は**分次についてのみ正しい**（ADR-0031 決定2）。
//
// 🔴 ADR-0043（計画）決定 1, #1030, IADR-0437: 暫定の前提値「約 300 回/日」は**撤回された**（公式に日次の記載が無く、
// 出典も今は書いていない）。日次上限は「公式に記載が無く、未実測」であり、**既定では何とも比べない**
// （<see cref="FinnhubDailyVolumeGuardOptions.ProvisionalDailyLimit"/> の既定は未設定）。上限を実測して設定したときだけ
// 比べる（ADR-0031 決定 3 の「統制（確定）」の文は有効なまま）。推測値を焼き込まない（IADR-0224）。
//
// ADR-0043（計画）決定 3: 1 日の巡回回数は**開場中の巡回だけ**で数える（<see cref="CyclesPerDay(int, int)"/> の
// activeMinutesPerDay）。全市場が閉じている間は巡回しないプロセス（市場監視）は場中の長さを渡し、24 時間巡回する
// プロセス（リスク管理の現在値の補充等）は既定の 24 時間のまま数える——数え方は巡回の形に合わせる。
//
// ADR-0031 決定4: 同一鍵を共有する全プロセスの見積りは合算する。<see cref="ApiKeyGroup"/> が
// 同一のプロセスだけを合算し、鍵が別のプロセスは独立に判定する（合算しない）。
public static class FinnhubDailyVolumeEstimator
{
    /// <summary>24 時間（分）。<see cref="CyclesPerDay(int, int)"/> の既定＝開場に関係なく巡回し続けるプロセス。</summary>
    public const int MinutesPerDay = 24 * 60;

    public enum Verdict
    {
        Within,
        Exceeds,

        /// <summary>ADR-0043 決定 1: 比べる日次上限が無い（未実測・未設定。既定）。見積りは記録するが判定しない。</summary>
        NotCompared,
    }

    /// <summary>1 プロセスぶんの日次要求見積りの入力。</summary>
    /// <param name="ProcessName">プロセス（サービス）名。ログ・自己申告での表示用。</param>
    /// <param name="ApiKeyGroup">
    /// 同一 Finnhub 鍵を共有するプロセスを束ねる識別子（例: API キーの SHA-256 ハッシュ）。
    /// 生の鍵値は持たせない——ADR-0031 決定4 の実測（IADR-0275）と同じく、値を露出せずに同一性だけを比較する。
    /// </param>
    /// <param name="SymbolCount">1 巡回で問い合わせる銘柄数。</param>
    /// <param name="RequestsPerSymbolPerCycle">1 巡回・1 銘柄あたりの要求数。</param>
    /// <param name="CyclesPerDay">1 日あたりの巡回回数（<see cref="CyclesPerDay(int, int)"/> 参照）。</param>
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

    /// <summary>
    /// 同一鍵（<see cref="ProcessVolume.ApiKeyGroup"/>）グループ 1 つぶんの合算結果。上限が無い（null）ときは
    /// <see cref="Verdict.NotCompared"/> で、<paramref name="ExceedRatio"/> も null。
    /// </summary>
    public readonly record struct KeyGroupEstimate(
        string ApiKeyGroup,
        long EstimatedDailyRequests,
        int? ProvisionalDailyLimit,
        Verdict Verdict,
        double? ExceedRatio,
        IReadOnlyList<string> ProcessNames);

    /// <summary>
    /// 巡回間隔（秒）から 1 日あたりの巡回回数を算出する（切り捨て）。<paramref name="activeMinutesPerDay"/> は 1 日のうち
    /// 巡回する時間（分）。既定は 24 時間（開場に関係なく巡回するプロセス）。ADR-0043 決定 3: 閉場中は巡回しないプロセスは
    /// 場中の長さ（米国 390 分）を渡す。
    /// </summary>
    public static int CyclesPerDay(int pollIntervalSeconds, int activeMinutesPerDay = MinutesPerDay)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pollIntervalSeconds);
        ArgumentOutOfRangeException.ThrowIfNegative(activeMinutesPerDay);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(activeMinutesPerDay, MinutesPerDay);
        return activeMinutesPerDay * 60 / pollIntervalSeconds;
    }

    /// <summary>単一プロセスの見積りを日次上限（未設定なら比べない）と突き合わせる。</summary>
    public static KeyGroupEstimate Evaluate(ProcessVolume process, int? provisionalDailyLimit) =>
        Evaluate([process], provisionalDailyLimit)[0];

    /// <summary>
    /// 既に算出済みの日次要求見積り（合計）を日次上限と直接突き合わせる（銘柄数等の内訳を要さない場合の簡易版）。
    /// 上限が未設定（null）なら比べない（<see cref="Verdict.NotCompared"/>）。
    /// </summary>
    public static KeyGroupEstimate Evaluate(long estimatedDailyRequests, int? provisionalDailyLimit, string processName = "")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedDailyRequests);
        var (verdict, ratio) = Judge(estimatedDailyRequests, provisionalDailyLimit);
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
    /// グループごとに日次上限と突き合わせる。<b>鍵が別（ApiKeyGroup が異なる）プロセスは合算しない。</b>
    /// </summary>
    public static IReadOnlyList<KeyGroupEstimate> Evaluate(
        IReadOnlyCollection<ProcessVolume> processes, int? provisionalDailyLimit)
    {
        ArgumentNullException.ThrowIfNull(processes);
        if (processes.Count == 0)
            return [];

        return processes
            .GroupBy(p => p.ApiKeyGroup, StringComparer.Ordinal)
            .Select(group =>
            {
                var total = group.Sum(p => p.EstimatedDailyRequests);
                var (verdict, ratio) = Judge(total, provisionalDailyLimit);
                return new KeyGroupEstimate(
                    group.Key, total, provisionalDailyLimit, verdict, ratio, [.. group.Select(p => p.ProcessName)]);
            })
            .ToArray();
    }

    // 上限が無ければ比べない。上限は正でなければならない（0 以下を「常に超過」として鳴らし続けない）。
    private static (Verdict Verdict, double? Ratio) Judge(long estimated, int? limit)
    {
        if (limit is not { } value)
            return (Verdict.NotCompared, null);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(limit));
        return (estimated > value ? Verdict.Exceeds : Verdict.Within, (double)estimated / value);
    }
}
