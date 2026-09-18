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
}
