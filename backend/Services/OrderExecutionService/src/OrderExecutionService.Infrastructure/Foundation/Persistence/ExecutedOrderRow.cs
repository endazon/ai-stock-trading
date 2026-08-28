using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Infrastructure.Persistence;

// #13 Slice A, FR-05, FR-16: 発注結果（注文実体＋スリッページ）の行モデル。月報・ポジション射影のデータ源。
// ADR-0001 の専有 DB に配置する。OrderId を主キーとする（ブローカ注文 ID）。
internal sealed class ExecutedOrderRow
{
    public string OrderId { get; set; } = string.Empty;

    public Guid DecisionId { get; set; }

    public string Symbol { get; set; } = string.Empty;

    public Market Market { get; set; }

    public TradeSide Side { get; set; }

    public ProductType ProductType { get; set; }

    public PositionEffect PositionEffect { get; set; }

    public int Quantity { get; set; }

    public decimal PlannedPrice { get; set; }

    public int FilledQuantity { get; set; }

    public decimal AveragePrice { get; set; }

    public OrderStatus Status { get; set; }

    public decimal SlippageRatio { get; set; }

    public DateTimeOffset ExecutedAt { get; set; }
}
