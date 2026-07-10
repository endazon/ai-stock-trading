using AiStockTrading.Report.Domain;

namespace AiStockTrading.Report.Worker.Foundation.Persistence;

// FR-06/07, IADR-0024: 報告書の行モデル。PeriodKey を主キー、Version を楽観排他トークンとする（IADR-0012 踏襲）。
// ADR-0001 の専有 DB（report_svc）に配置する。数値集計列（FR-16）は後続スライスで拡充する。
internal sealed class ReportRow
{
    public string PeriodKey { get; set; } = string.Empty;

    public ReportKind Kind { get; set; }

    public DateOnly PeriodStart { get; set; }

    public ReportState State { get; set; }

    public string? BasedOn { get; set; }

    public int AssumptionsVersion { get; set; }

    public string PolicySummary { get; set; } = string.Empty;

    public DateTimeOffset? ConfirmedAt { get; set; }

    public int Version { get; set; }
}
