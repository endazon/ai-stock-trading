using AiStockTrading.OrderExecution.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.OrderExecution.Infrastructure.Foundation.Persistence;

// FR-10, #331, IADR-0210 決定6: 保護逆指値レグの行モデル。EntryDecisionId 主キー（1 エントリー = 高々 1 保護。
// 再発注は同キーの上書き＝最新試行のみを保持する）。ProtectiveStopGuard の巡回対象（State=Active）の権威。
internal sealed class ProtectiveStopOrderRow
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
}
