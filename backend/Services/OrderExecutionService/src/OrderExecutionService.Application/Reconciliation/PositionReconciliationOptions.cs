namespace OrderExecutionService.Application.Reconciliation;

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
}
