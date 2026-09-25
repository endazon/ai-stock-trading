using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Infrastructure.Persistence;

// FR-10, #331, IADR-0210 決定6: 保護逆指値レグの行モデル。EntryDecisionId 主キー（1 エントリー = 高々 1 保護。
// 再発注は同キーの上書き＝最新試行のみを保持する）。ProtectiveStopGuard の巡回対象（State=Active）の権威。
public sealed class ProtectiveStopOrderRow
{
    public Guid EntryDecisionId { get; set; }

    public Guid StopDecisionId { get; set; }

    public string StopOrderId { get; set; } = string.Empty;

    public string Symbol { get; set; } = string.Empty;

    public Market Market { get; set; }

    public TradeSide EntrySide { get; set; }

    public ProductType ProductType { get; set; }

    public BrokerProvider Mode { get; set; }

    public int Quantity { get; set; }

    public decimal TriggerPrice { get; set; }

    public decimal FxRateToBase { get; set; }

    public int Attempt { get; set; }

    public ProtectiveStopState State { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    // FR-10, ADR-0040 決定1（S1）, #820, IADR-0344 決定1: 保護の機構（既定 0＝S0）と損切りライン到達の記録（S1 のみ）。
    public StopLossExecutionMethod Mechanism { get; set; } = StopLossExecutionMethod.BrokerStopOrder;

    public DateTimeOffset? TriggeredAt { get; set; }

    public decimal? TriggeredPrice { get; set; }

    // FR-10, #820 の 4 巡目監査, IADR-0344 追記(4): 残保護数量（null＝エントリーの約定が未確定）と、
    // 到達済みなのに決済できない状態を Critical で知らせた時刻（1 行 1 回）。
    public int? RemainingProtected { get; set; }

    public DateTimeOffset? StalledNotifiedAt { get; set; }

    // FR-10, #820 の 5 巡目監査, IADR-0344 追記(5): 外部要因で削ったが**まだ確定していない**株数と、その連続観測回数。
    // 建玉照会が 1 巡回だけ過少に見えただけで行を失わないための門（確定するまで完了させない・S0 の逆指値も取り消さない）。
    public int PendingExternalReduction { get; set; }

    public int ExternalReductionObservations { get; set; }

    // FR-10, #820 の 8 巡目監査, IADR-0344 追記(8): 超過が**消えた**ことの連続観測回数（確定と対称の失効）と、
    // 実効数量が 0 になった時刻・Critical で知らせた時刻（1 行 1 回。無音で保護が失われるのを止める）。
    public int ExternalReductionAbsences { get; set; }

    public DateTimeOffset? ProtectionSuspendedSince { get; set; }

    public DateTimeOffset? ProtectionSuspendedNotifiedAt { get; set; }

    // FR-10, #820 の 10 巡目監査, IADR-0344 追記(9): 「帰属不明の建玉」を最後に知らせた株数と時刻
    // （群の代表行が持つ。同じ状態で毎巡回鳴らさない・再起動で Critical を再送しない）。
    public int? UnattributedNotifiedQuantity { get; set; }

    public DateTimeOffset? UnattributedNotifiedAt { get; set; }

    // FR-10, #833 項目2, IADR-0344 追記(15): S1 の決済の連続失敗回数・次の成行を送ってよい最早時刻・最後に受けた到達の検知時刻
    // （行ごとの待ち時間。既存行は 0 / null / null＝待ち時間なし）。
    public int CloseFailures { get; set; }

    public DateTimeOffset? NextCloseAttemptAt { get; set; }

    public DateTimeOffset? LastTriggerSeenAt { get; set; }
}
