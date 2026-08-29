namespace OrderExecutionService.Features.OrderExecution;

// #141, FR-05, IADR-0074: 発注予約の自動リコンサイル（Reserved 滞留の解消）の構成。
//
// **既定は無効（Enabled=false）**。リポの fail-safe 既定（Broker=paper・外部連携空=no-op・retention も無効）に
// 合わせた明示オプトイン。有効化しても既定の no-op プローブ（IndeterminateReservationBrokerProbe）下では
// Placed/NotPlaced 経路は発火せず、phase-4 自己修復のみ（ブローカ非依存）が作動する。
public sealed class ReconciliationOptions
{
    public const string SectionName = "Reconciliation";

    /// <summary>リコンサイルを有効にするか（fail-safe 既定: 無効＝走査しない）。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// #141, IADR-0092: 実照会プローブ（moomoo/OpenD SIMULATE）を使うか（fail-safe 既定: false＝no-op プローブ）。
    /// true かつ Broker:Provider=moomoo のときだけ実プローブが配線される。paper／OpenD 無しでは無視され、
    /// 既定の no-op プローブ（常に Indeterminate＝何も解放・終端化しない）のままになる。
    /// </summary>
    public bool UseBrokerProbe { get; set; }

    /// <summary>
    /// 滞留とみなす閾値（時間）。既定 24 時間。<see cref="ReconciliationPolicy.MinimumStallThresholdHours"/>
    /// 未満を設定しても下限クランプされるため、in-flight の予約に触れることはない。
    /// </summary>
    public int StallThresholdHours { get; set; } = ReconciliationPolicy.DefaultStallThresholdHours;

    /// <summary>巡回間隔（時間）。既定 6 時間。</summary>
    public int IntervalHours { get; set; } = 6;

    /// <summary>1 巡回で処理する最大件数。</summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>
    /// 巡回間隔。下限 1 時間（設定ミスで高頻度に回らないため）／上限 1 年（<see cref="TimeSpan.FromHours(double)"/> の
    /// オーバーフローで BackgroundService 起動時に落ちるのを防ぐため）。
    /// </summary>
    public TimeSpan Interval => TimeSpan.FromHours(Math.Clamp(IntervalHours, 1, MaximumIntervalHours));

    /// <summary>巡回間隔の上限（時間）。1 年。これを超える間隔は「実質無効」であり、無効化は Enabled で行う。</summary>
    public const int MaximumIntervalHours = 24 * 365;

    /// <summary>1 巡回の処理上限（下限 1・上限 10000）。</summary>
    public int EffectiveBatchSize => Math.Clamp(BatchSize, 1, 10_000);
}
