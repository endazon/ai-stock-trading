namespace OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;

// FR-10, #331, IADR-0210 決定4: 保護逆指値ガード（失効検知・再発注・残存取消）の巡回設定。
// 既定有効——無効化は「逆指値なしの建玉を検知しない」ことを明示的に選ぶ運用判断である。
public sealed class ProtectiveStopGuardOptions
{
    public const string SectionName = "ProtectiveStopGuard";

    public bool Enabled { get; init; } = true;

    /// <summary>巡回間隔。失効から再発注までの最大遅延がこの間隔になる（短いほど保護の穴が狭い）。</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>1 巡回で評価する Active 記録の最大件数（保有建玉数上限 3 に対し十分大きい既定）。</summary>
    public int BatchSize { get; init; } = 50;

    /// <summary>
    /// FR-10, #902, IADR-0365 決定5: Active なソフトウェア逆指値（S1）の要約（Information）を出す間隔。
    /// ストアもこの間隔に 1 回だけ読む（S1 が無いときも同じ）。
    /// </summary>
    public TimeSpan SoftwareStopSummaryInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// FR-10, #1093, IADR-0458: 建玉照会が分類できた一時的な失敗のとき、巡回の中で照会し直す最大回数（既定 1・0〜1 に収める）。
    /// 0 で無効（従来どおり 1 回の失敗で巡回ごと据え置く）。失敗も頻度制限の枠を消費するため 2 回以上は許さない（IADR-0144 決定 5）。
    /// </summary>
    public int PositionQueryMaxRetries { get; init; } = 1;

    /// <summary>FR-10, #1093, IADR-0458: 一時的な失敗（打ち切り・切断・接続の確立の失敗）の後の待ち（既定 2 秒）。</summary>
    public TimeSpan PositionQueryTransientRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>FR-10, #1093, IADR-0458: 頻度制限の後の待ち（既定 10 秒。OpenD の制限の窓は 30 秒程度）。</summary>
    public TimeSpan PositionQueryRateLimitedRetryDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>FR-10, #1093, IADR-0458: 待ちの揺らぎの幅（既定 ±50%。0〜1 に収める）。</summary>
    public double PositionQueryRetryJitter { get; init; } = 0.5;

    public int PositionQueryMaxRetriesClamped => Math.Clamp(PositionQueryMaxRetries, 0, 1);

    public double PositionQueryRetryJitterClamped => double.IsFinite(PositionQueryRetryJitter)
        ? Math.Clamp(PositionQueryRetryJitter, 0.0, 1.0)
        : 0.0;

    /// <summary>
    /// FR-10, #1093, IADR-0458: 照会し直しの待ちの合計の上限＝巡回間隔の半分。これを超えるなら照会し直さずに据え置く
    /// （30 秒の巡回なら 15 秒）。数えるのは待ちだけで、照会の所要時間は含まない（打ち切りが 2 回続けば次の巡回は十数秒遅れ得る）。
    /// </summary>
    public TimeSpan PositionQueryRetryBudget => Interval > TimeSpan.Zero ? Interval / 2 : TimeSpan.Zero;
}
