using ReportService.Domain;

namespace ReportService.Infrastructure.Persistence;

// FR-06/07, IADR-0024: 報告書の行モデル。PeriodKey を主キー、Version を楽観排他トークンとする（IADR-0012 踏襲）。
// ADR-0001 の専有 DB（report_svc）に配置する。数値集計列（FR-16）は後続スライスで拡充する。
public sealed class ReportRow
{
    public string PeriodKey { get; set; } = string.Empty;

    public ReportKind Kind { get; set; }

    public DateOnly PeriodStart { get; set; }

    public ReportState State { get; set; }

    // FR-07, IADR-0042/0071 決定5: 対話的確定のレビュー局面（Draft の下位状態を精緻化）。
    // Draft 局面は Drafting/PendingApproval/ChangesRequested のいずれか、確定済みは Confirmed に対応する。
    public ReviewState ReviewState { get; set; }

    public string? BasedOn { get; set; }

    public int AssumptionsVersion { get; set; }

    public string PolicySummary { get; set; } = string.Empty;

    // FR-06, IADR-0115, #280: 報告書本文（Markdown）。既存行は NULL＝読み出し時に空文字（後方互換）。
    public string? Body { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }

    public int Version { get; set; }
}
