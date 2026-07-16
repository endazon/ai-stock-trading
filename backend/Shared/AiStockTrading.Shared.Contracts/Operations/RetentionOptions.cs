namespace AiStockTrading.Shared.Contracts.Operations;

// NFR（運用）, #137, IADR-0059: 重複排除ストアのパージジョブの構成。費用統制（processed_messages）と
// 発注執行（order_dispatch_reservations の終端行）の双方が同じ節・同じ既定値を使うための単一情報源。
//
// **既定は無効（Enabled=false）**。DELETE は不可逆であり、リポの fail-safe 既定（Broker=paper・外部連携空=no-op）
// に合わせて明示的なオプトインとする（IADR-0059 決定4）。有効化手順は docs/operations/operations.md 参照。
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>パージジョブを有効にするか（fail-safe 既定: 無効＝1 行も消さない）。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 保持期間（日）。既定 90 日。<see cref="RetentionPolicy.MinimumRetentionDays"/> 未満を設定しても
    /// 下限でクランプされるため、設定ミスで重複排除を壊すことはない（IADR-0059 決定3）。
    /// </summary>
    public int RetentionDays { get; set; } = RetentionPolicy.DefaultRetentionDays;

    /// <summary>巡回間隔（時間）。既定 24 時間（日次）。</summary>
    public int IntervalHours { get; set; } = 24;

    /// <summary>1 巡回で削除する最大行数。バッチ削除のメモリ上限になる。</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>巡回間隔（下限 1 時間・設定ミスで高頻度に回らないようにする）。</summary>
    public TimeSpan Interval => TimeSpan.FromHours(Math.Max(1, IntervalHours));

    /// <summary>1 巡回の削除上限（下限 1・上限 10000）。</summary>
    public int EffectiveBatchSize => Math.Clamp(BatchSize, 1, 10_000);
}
