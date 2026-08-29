using OrderExecutionService.Domain;

namespace OrderExecutionService.Infrastructure.Persistence;

// #154, FR-05, FR-19, FR-11, IADR-0067: 発注済み注文に起きた訂正・取消の行モデル（追記専用）。
// ADR-0001 の専有 DB に配置する。発注（Place）と終端結果は ExecutedOrderRow が権威のため、本表は訂正・取消のみ。
// 訂正は前後の値を保持し、取消は数量・価格を持たない（いずれも nullable）。
public sealed class OrderLifecycleEventRow
{
    public Guid Id { get; set; }

    public Guid DecisionId { get; set; }

    public string OrderId { get; set; } = string.Empty;

    public OrderLifecycleKind Kind { get; set; }

    public int? PreviousQuantity { get; set; }

    public decimal? PreviousPrice { get; set; }

    public int? Quantity { get; set; }

    public decimal? Price { get; set; }

    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; set; }
}
