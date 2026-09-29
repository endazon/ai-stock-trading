namespace OrderExecutionService.Features.OrderExecution.ObserveBrokerPositions;

// #292, FR-05, FR-10, IADR-0118: ブローカ建玉スナップショットの定期発行の構成。
//
// 既定有効は意図的（IADR-0113 と同じ理由）: 副作用は**読み取り照会のみ**で、発注・訂正・取消を 1 つも増やさない。
// 検知器を既定オフで出荷することは「乖離が見えない状態」を既定にすることを意味する。
// paper では IBrokerPositionSource が DI に存在せず、常駐が起動時に自己停止する（構造的な非干渉）。
public sealed class PositionReconciliationOptions
{
    public const string SectionName = "Reconciliation:Positions";

    private const int MinIntervalSeconds = 60;
    private const int MaxIntervalSeconds = 3600;
    private const int DefaultIntervalSeconds = 600;
    private const int DefaultInitialDelaySeconds = 20;

    public bool Enabled { get; set; } = true;

    public int IntervalSeconds { get; set; } = DefaultIntervalSeconds;

    /// <summary>
    /// 巡回間隔。未設定・非正は既定（10 分）へ、範囲外は 60〜3600 秒へクランプする。
    /// 下限は照会の連打を防ぎ、上限は「事実上止まっている」設定を作らせないため。
    /// </summary>
    public TimeSpan Interval => TimeSpan.FromSeconds(
        IntervalSeconds <= 0
            ? DefaultIntervalSeconds
            : Math.Clamp(IntervalSeconds, MinIntervalSeconds, MaxIntervalSeconds));

    // FR-10, #1093, IADR-0459 決定1・3: 起動直後の初回の建玉照会を、ガード（即時）・稼働 probe（既定 10 秒）とずらす。
    public int InitialDelaySeconds { get; set; } = DefaultInitialDelaySeconds;

    /// <summary>
    /// 起動から初回の照会までの遅延（既定 20 秒・揺らぎなし）。負の値は 0（遅らせない）へ、巡回間隔を超える値は巡回間隔へクランプする。
    /// 上限は「設定できるが初回が来ない」状態を作らせないため。2 回目以降の巡回の間隔は変えない。
    /// </summary>
    public TimeSpan InitialDelay => TimeSpan.FromSeconds(
        Math.Clamp(InitialDelaySeconds, 0, (int)Interval.TotalSeconds));
}
