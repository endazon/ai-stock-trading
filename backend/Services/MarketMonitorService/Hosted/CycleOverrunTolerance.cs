namespace MarketMonitorService.Hosted;

// FR-04, NFR-01, ADR-0043（計画）決定 2 (b), #1281, IADR-0513: 巡回の所要の Warning に足す余裕（巡回間隔 ＋ 余裕を超えたら警告する）。
//
// (b) の判定（n × 60 ≤ r × 間隔）が「収まる」とした境界の構成は、容量 1・等間隔の限流器（IADR-0513）が巡回をまたいで間隔を保つため、
// 所要 ≒ 巡回間隔 ± 往復の揺らぎで回る。間隔ちょうどで警告すると境界の構成で恒常的に鳴る（#1281 の PoC 観測）。
// 余裕は限流器の 1 要求ぶんの送出間隔（⌈1 分 ÷ r⌉。`FinnhubRateLimiter.CreateBucket` と同じティック切り上げ）とする。
// これを超えた巡回は、少なくとも 1 要求ぶんの時間を限流器の外（保有の照会・往復・発行）で失っている＝(b) の想定が崩れた印である。
// Finnhub 以外の提供元（限流器が律速しない）では余裕 0。
public sealed record CycleOverrunTolerance(TimeSpan Value)
{
    public static CycleOverrunTolerance None { get; } = new(TimeSpan.Zero);

    /// <summary>
    /// 構成の提供元と自制レート（回/分）から余裕を決める。提供元の判定は <c>WatchlistCycleFitGuard.Applies</c> と同じ
    /// （大小文字・前後の空白を無視）。0 以下の自制レートは 1 回/分へ寄せる（限流器と同じ）。
    /// </summary>
    public static CycleOverrunTolerance For(string? provider, int requestsPerMinute)
    {
        if (!string.Equals(provider?.Trim(), "finnhub", StringComparison.OrdinalIgnoreCase))
            return None;

        var rate = Math.Max(1, requestsPerMinute);
        return new(TimeSpan.FromTicks((TimeSpan.TicksPerMinute + rate - 1) / rate));
    }
}
