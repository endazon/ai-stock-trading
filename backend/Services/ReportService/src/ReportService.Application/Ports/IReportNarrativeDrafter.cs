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
//
// FR-07, IADR-0117 決定3, #293, 04_workflows/03_reporting-cycle:
// ParentPeriodKey / ParentPolicySummary は**上位方針**（日報なら当週の週報・週報なら当月の月報・
// 月報なら前月の月報）の期間キーと本文。計画の業務フローが求める「上位方針の目標との差異評価」は
// 本文が無ければ書けないため、期間キーだけでなく本文まで渡す。
// いずれも null 可＝上位が未確定であることを表す（プロンプト側でその旨を明記する＝捏造しない）。
//
// 上位方針は**散文の文脈としてのみ**用いる。PolicySummary（確定すると取引に効くフィールド）へは
// 混ぜない（IADR-0115 決定4「自動生成では新しい方針を機械に提案させない」・ADR-0003）。
public sealed record ReportNarrativeContext(
    ReportKind Kind,
    string PeriodKey,
    string PeriodLabel,
    IReadOnlyList<string> Markets,
    PnlSummary Pnl,
    string PolicySummary,
    string? ParentPeriodKey = null,
    string? ParentPolicySummary = null);
