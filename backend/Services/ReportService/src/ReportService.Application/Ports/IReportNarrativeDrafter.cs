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
// FR-07, IADR-0120 決定3, #293, 04_workflows/03_reporting-cycle:
// ParentPolicy は**上位方針**（日報なら当週の週報・週報なら当月の月報・月報なら前月の月報）。
// null 可＝上位が未確定であることを表す（プロンプト側でその旨を明記する＝捏造しない）。
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
    ParentPolicyReference? ParentPolicy = null);

// FR-07, IADR-0120 決定3: 上位方針の参照（期間キーと本文）。
// 期間キーと本文を 1 つの record に束ねることで「片方だけ在る」状態を表現不能にする
// （計画の業務フローが求める「上位方針の目標との差異評価」は本文が要り、出典を示すには期間キーが要る。
// どちらが欠けても意味をなさないため、欠損の表現は record ごと null の 1 通りに閉じる）。
public sealed record ParentPolicyReference(string PeriodKey, string Summary);
