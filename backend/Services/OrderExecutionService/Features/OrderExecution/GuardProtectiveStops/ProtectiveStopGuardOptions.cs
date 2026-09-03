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
}
