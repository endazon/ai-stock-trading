using ReportService.Domain;

namespace ReportService.Features.Reports.DraftReport;

internal static class DraftReportEndpoint
{
    // FR-06/16, IADR-0032: 報告書ドラフト生成（日報/週報/月報・数値はコード集計・散文は LLM ドラフト）。生成のみで永続化しない。
    // 前提条件は暫定既定値（#19 のバージョン付き取得・#63 台帳連携は #22 後続）。
    public static void MapDraftReport(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/{periodKey}/draft", async (string periodKey, DraftReportRequest req, ReportDraftService svc, CancellationToken ct) =>
        {
            // FR-07, IADR-0120 決定5: 上位方針の本文は任意（省略時 null＝上位なし）。自動生成が主経路であり、
            // 手動経路は呼び出し側が文脈を持つ場合のための穴に留める（既存の呼び出しは非破壊）。
            var draft = await svc.BuildDraftAsync(new DraftRequest(
                req.Kind, periodKey, req.Date, req.Markets, req.AssumptionsVersion, req.BasedOn,
                req.PolicySummary ?? string.Empty, req.Fills, req.CurrentPrices,
                ParentPolicySummary: req.ParentPolicySummary), ct);
            return Results.Ok(new { periodKey, markdown = draft.Markdown, pnl = draft.Pnl });
        });
}

// 報告書ドラフト生成の要求（数値はコード集計・散文は LLM ドラフト）。Kind で日報/週報/月報を切り替える。
// Fills は集計対象の約定列（#63 台帳連携は #22 後続）。
// FR-07, IADR-0120 決定5: ParentPolicySummary は上位方針（BasedOn が指す報告書）の本文。任意（既定 null）。
internal sealed record DraftReportRequest(
    ReportKind Kind,
    DateOnly Date,
    List<string>? Markets,
    int AssumptionsVersion,
    string? BasedOn,
    string? PolicySummary,
    List<PeriodTradeFill>? Fills,
    Dictionary<string, decimal>? CurrentPrices,
    string? ParentPolicySummary = null);
