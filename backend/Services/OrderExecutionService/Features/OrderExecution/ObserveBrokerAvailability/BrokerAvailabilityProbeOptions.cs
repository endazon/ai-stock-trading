namespace OrderExecutionService.Features.OrderExecution.ObserveBrokerAvailability;

// FR-20, FR-05, #385, 06_daytrading-review §4.2, IADR-0150: ブローカ稼働 probe（Stage 1 の稼働分数の供給元）の構成。
//
// 既定有効は意図的（PositionReconciliationOptions・IADR-0113 と同じ理由）: 副作用は**読み取り照会のみ**で、
// 発注・訂正・取消を 1 つも増やさない。既定オフで出荷すると「Stage 1 の期間が一生 0 のまま」が既定になり、
// #333 以来の「ゲートが実効化していない」状態がそのまま残る。
public sealed class BrokerAvailabilityProbeOptions
{
    public const string SectionName = "Stage1:UptimeProbe";

    private const int MinIntervalSeconds = 60;
    private const int MaxIntervalSeconds = 1800;
    private const int DefaultIntervalSeconds = 300;
    private const int DefaultInitialDelaySeconds = 10;

    /// <summary>稼働 probe を回すか（既定: 有効）。無効化すると Stage 1 の営業日は 1 日も積まれない。</summary>
    public bool Enabled { get; set; } = true;

    public int IntervalSeconds { get; set; } = DefaultIntervalSeconds;

    /// <summary>
    /// 巡回間隔。未設定・非正は既定（5 分）へ、範囲外は 60〜1800 秒へクランプする。
    /// <para>
    /// 下限 60 秒は OpenD への照会の連打を防ぐため。上限 1800 秒（30 分）は、受け手が 1 件の観測に
    /// 認める最大の遡り（<c>Stage1SessionUptime.MaximumCreditMinutesPerObservation</c>）と同じであり、
    /// これを超える間隔を設定すると**観測が 1 件も credit されなくなる**（区間が上限を超えるため）。
    /// 「設定できるが何も起こらない」という状態を作らないためにクランプする。
    /// </para>
    /// </summary>
    public TimeSpan Interval => TimeSpan.FromSeconds(
        IntervalSeconds <= 0
            ? DefaultIntervalSeconds
            : Math.Clamp(IntervalSeconds, MinIntervalSeconds, MaxIntervalSeconds));

    // FR-10, #1093, IADR-0459 決定1・3: 起動直後の初回の建玉照会（到達性の判定に流用）を、ガード（即時）・スナップショット（既定 20 秒）とずらす。
    public int InitialDelaySeconds { get; set; } = DefaultInitialDelaySeconds;

    /// <summary>
    /// 起動から初回の probe までの遅延（既定 10 秒・揺らぎなし）。負の値は 0（遅らせない）へ、巡回間隔を超える値は巡回間隔へクランプする。
    /// <para>
    /// FR-20, IADR-0459 結果: 再起動をはさむ区間がこの分だけ長くなり、巡回間隔を超えればその区間は稼働として積まれない
    /// （積み不足の側。水増しには倒れない）。2 回目以降の巡回の間隔は変えない。
    /// </para>
    /// </summary>
    public TimeSpan InitialDelay => TimeSpan.FromSeconds(
        Math.Clamp(InitialDelaySeconds, 0, (int)Interval.TotalSeconds));
}
