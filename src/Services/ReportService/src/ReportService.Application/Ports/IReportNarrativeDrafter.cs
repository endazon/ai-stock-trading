using AiStockTrading.Report.Domain;

namespace AiStockTrading.Report.Application.Ports;

// FR-06/16, IADR-0032: 報告書の散文（市況・振り返り・評価等）を LLM でドラフトするポート。数値は含めない（数値はコード集計・FR-16）。
// 実データは platform LLM ゲートウェイ経由（後続）。本スライスは安全既定プレースホルダ＋テストの fake で供給する。
public interface IReportNarrativeDrafter
{
    Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default);
}

// 散文ドラフトの文脈（LLM プロンプトの素材）。数値は集計済みの参考値として渡すが、LLM に再計算はさせない（提示のみ）。
// Kind/PeriodLabel/Markets で対象種別・期間・市場を踏まえた散文を書けるようにする。
public sealed record ReportNarrativeContext(
    ReportKind Kind,
    string PeriodKey,
    string PeriodLabel,
    IReadOnlyList<string> Markets,
    PnlSummary Pnl,
    string PolicySummary);
