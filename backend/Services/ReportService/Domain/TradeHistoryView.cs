using AiStockTrading.Shared.Contracts.Trading;

namespace ReportService.Domain;

// FR-16, 04_report-templates 日報 §2: 取引を発生させたトリガー種別（定時サイクル/価格変動/損切り機械執行）。
//
// 🔴 **#563 / IADR-0269: 現時点でこの値を供給できる記録は存在しない。**
// 判断の起点（TradeDecisionService の `DecisionTriggerKind`）は**プロセス内にしか無く**、
// `TradeDecisionMade` にも `OrderIntent` にも列が無い。したがって `TradeHistoryLine.Trigger` は
// `null`（未供給）になる。**「定時」を既定にしない**——起点を知らないことを「定時だった」と書けば嘘になる。
public enum TradeTrigger
{
    /// <summary>定時サイクル（スケジュール起動）。</summary>
    Scheduled,

    /// <summary>価格変動トリガー。</summary>
    PriceMovement,

    /// <summary>損切りライン到達による機械執行。</summary>
    StopLoss,
}

// FR-16, 04_report-templates 日報 §2「取引履歴（全明細）」の 1 約定＝1 行。数値は #63 台帳のコード集計値（LLM に計算させない）。
//
// 🔴 **#563 / IADR-0269: nullable は「未供給」であり、0・空文字・既定値ではない。**
// `SymbolName`（台帳が名称を持たない）・`Tax`（税は期間合計にのみ課され約定単位へ配分できない）・
// `Trigger`（起点が記録されていない）・`RationaleSummary`（判断記録を相関できない約定）が該当する。
// レンダラは `**未供給**` と描き、表の直下の凡例で「該当なし」「0」と区別する。
public sealed record TradeHistoryLine(
    int Index,
    TimeOnly Time,
    Market Market,
    string SymbolCode,
    string? SymbolName,
    TradeSide Side,
    int Quantity,
    decimal FillPrice,
    decimal Cost,
    decimal? Tax,
    decimal RealizedPnl,
    TradeTrigger? Trigger,
    string? RationaleSummary);

// FR-16, 04_report-templates 日報 §2「取引詳細（選定・売買の判断理由）」の 1 取引＝1 ブロック。
//
// 🔴 **#563 / IADR-0269: 5 項目を分けて持つ記録源はまだ無い。**
// `TradeDecisionMade.Rationale` は**単一の自由文**であり、5 項目へ割り付けると構造を捏造することになる。
// したがって `TradeHistoryView.Details` は `null`（未供給）になり、根拠は §2 の「判断根拠（要約）」列に出る。
public sealed record TradeDetailBlock(
    int Index,
    TimeOnly Time,
    string SymbolLabel,
    TradeSide Side,
    string SelectionReason,
    string DecisionReason,
    string ReferencedInfo,
    string Scenario,
    string ResultEvaluation);

// FR-16, 04_report-templates 日報 §2「見送り判断（主要なもの）」の 1 件。
//
// 🔴 **#563 / IADR-0269: 見送り（Hold）はイベント化されておらずログにしか残らない**
//（`TradeDecisionAppService` の Hold 分岐は `return null` する）。台帳に 1 件も無いため
// `TradeHistoryView.Skipped` は `null`（未供給）になる。**「（見送りなし）」と書くと嘘になる。**
public sealed record SkippedDecision(TimeOnly Time, string Symbol, string Reason);

// FR-16, 04_report-templates 日報 §2 の入力（全明細＋取引詳細＋見送り判断）。
//
// 🔴 **空列と `null` を潰さない**（本サービスの既存の規律と同じ）。
// - `Lines`: **空列＝当日の約定なし**。約定の供給は不達でも空列へ倒すと確定している（IADR-0115 決定5）ため
//   nullable にしない——「約定 0 件」は §1 サマリの取引回数 0 と整合する事実である。
// - `Details` / `Skipped`: **`null`＝供給元がない／空列＝該当なし**。
public sealed record TradeHistoryView
{
    public IReadOnlyList<TradeHistoryLine> Lines { get; init; } = [];

    /// <summary><c>null</c> ＝取引詳細の記録源が無い（未供給）。空列＝該当する取引が無い。</summary>
    public IReadOnlyList<TradeDetailBlock>? Details { get; init; }

    /// <summary><c>null</c> ＝見送り判断の記録源が無い（未供給）。空列＝見送りなし。</summary>
    public IReadOnlyList<SkippedDecision>? Skipped { get; init; }
}
