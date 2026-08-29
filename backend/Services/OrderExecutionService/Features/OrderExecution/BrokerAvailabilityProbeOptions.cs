namespace OrderExecutionService.Features.OrderExecution;

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
}
