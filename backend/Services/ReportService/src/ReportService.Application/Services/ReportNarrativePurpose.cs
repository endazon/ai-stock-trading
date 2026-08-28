using ReportService.Domain;

namespace ReportService.Application.Services;

// FR-06/11, IADR-0120 決定1, #291, 04_workflows/03_reporting-cycle:
// 報告書は取引方針を「月報→週報→日報→取引」の階層で管理する方針書であり、上位ほど難度が高い。
// 種別ごとに用途（purpose）を分けることで、基盤（platform LLM ゲートウェイ）の
// `Llm:Routing:PurposeModels` が種別ごとのモデルを解決できる（月報=最難関 / 日報=定型）。
//
// 純関数として Application に置くのは、実 HTTP を伴う Worker 実装（HttpReportNarrativeDrafter）から
// 分離して 3 種別の期待値を単体テストで固定するためである（ReportNarrativePromptBuilder と同じ方針）。
//
// ⚠️ 返す文字列は基盤の `PurposeModels` のキーと**一致していなければならない**。不一致だと
// `LlmRouter.ResolveModel` が未知 purpose として扱い、例外もログも出さずに `DefaultModel` へ
// フォールバックする＝割当が無音で失効する（platform IADR-0102 / IADR-0106 / IADR-0112 の罠）。
public static class ReportNarrativePurpose
{
    public static string For(ReportKind kind) => kind switch
    {
        ReportKind.Weekly => "report-weekly",
        ReportKind.Monthly => "report-monthly",
        _ => "report-daily",
    };
}
