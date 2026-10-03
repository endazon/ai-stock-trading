using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Infrastructure.Persistence;

// #13 Slice A, FR-05, FR-16: 発注結果（注文実体＋スリッページ）の行モデル。月報・ポジション射影のデータ源。
// ADR-0001 の専有 DB に配置する。OrderId を主キーとする（ブローカ注文 ID）。
public sealed class ExecutedOrderRow
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

    // 🔴 FR-10, FR-11, #1048, IADR-0481 決定3: 約定追跡の打ち切りを監査へ記録した、その追跡の起点（ExecutedAt の値）。
    // null＝打ち切りを記録していない。起点が後から進められた（RenewTracking）記録は、この値と ExecutedAt が食い違うため
    // 再び期限を過ぎたときに改めて打ち切りを記録する。**列の追加だけ**（既存行は null）。
    public DateTimeOffset? TrackingAbandonedFrom { get; set; }

    // 🔴 FR-10, ADR-0049 決定1, #1122, IADR-0486 決定6: 新規建ての損切り幅に下限を掛けてラインを引いた印（出所の序数。1＝Fallback2Pct / 2＝Atr14）。
    // null＝分からない（列を足す前の行・決済・保護レグ）。**列の追加だけ**（既存行は null＝従来どおり遡及の判定をする）。
    public StopWidthFloorSource? StopFloorSource { get; set; }
}
