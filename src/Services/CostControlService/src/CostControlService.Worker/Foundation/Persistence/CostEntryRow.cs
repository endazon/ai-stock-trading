using AiStockTrading.CostControl.Domain;

namespace AiStockTrading.CostControl.Worker.Foundation.Persistence;

// NFR（費用）, IADR-0027: 費用計上の行モデル（追記専用）。専有 DB（cost_control_svc）に配置する。Month は "yyyy-MM"。
internal sealed class CostEntryRow
{
    public Guid Id { get; set; }

    public string Month { get; set; } = string.Empty;

    public CostCategory Category { get; set; }

    public decimal Amount { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
}
