namespace OrderExecutionService.Features.OrderExecution.PollOrderFills;

// #270, FR-05, FR-10, IADR-0113: 約定状態の追跡ポーリングの構成。
//
// **既定は有効（Enabled=true）**。リポの「新規の常駐処理は既定オフ」慣行からの意図的な逸脱で、理由は
// (1) 本ポーリングは統制（FR-10）が実効するための必要条件であり、既定オフは「統制が効かない状態」を
// 既定として出荷することになる、(2) 副作用はブローカ状態の**読み取り照会のみ**（発注・訂正・取消を増やさない）、
// (3) paper では構造的に動かない（Program.cs が moomoo 選択時のみ登録し、かつ paper は非終端記録を作らない）。
// 停止は Enabled=false の 1 設定で足りる。
public sealed class FillPollingOptions
{
    public const string SectionName = "FillPolling";

    /// <summary>巡回間隔の下限（秒）。ブローカへの照会が過剰にならない最小値。</summary>
    public const int MinimumIntervalSeconds = 5;

    /// <summary>巡回間隔の上限（秒＝1 時間）。これを超える間隔は「実質無効」であり、無効化は Enabled で行う。</summary>
    public const int MaximumIntervalSeconds = 3600;

    /// <summary>追跡上限の上限（時間＝7 日）。</summary>
    public const int MaximumTrackingHours = 24 * 7;

    /// <summary>約定状態の追跡を有効にするか（既定: 有効）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>巡回間隔（秒）。既定 30 秒（判断サイクル 5 分に対して十分細かい）。</summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>
    /// 発注からこの時間を過ぎた非終端記録は追跡対象から外す（既定 24 時間）。
    /// 以降は滞留として人手／リコンサイル（IADR-0074）の領分に戻す。
    /// </summary>
    public int MaxTrackingHours { get; set; } = 24;

    /// <summary>1 巡回で照会する最大件数。</summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>巡回間隔（下限 5 秒・上限 1 時間でクランプ）。</summary>
    public TimeSpan Interval =>
        TimeSpan.FromSeconds(Math.Clamp(IntervalSeconds, MinimumIntervalSeconds, MaximumIntervalSeconds));

    /// <summary>追跡上限（下限 1 時間・上限 7 日でクランプ）。</summary>
    public TimeSpan MaxTracking => TimeSpan.FromHours(Math.Clamp(MaxTrackingHours, 1, MaximumTrackingHours));

    /// <summary>1 巡回の処理上限（下限 1・上限 10000）。</summary>
    public int EffectiveBatchSize => Math.Clamp(BatchSize, 1, 10_000);
}
