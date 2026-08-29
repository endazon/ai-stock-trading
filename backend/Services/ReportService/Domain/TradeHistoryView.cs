using AiStockTrading.Shared.Contracts.Trading;

namespace ReportService.Domain;

// FR-16, 04_report-templates 日報 §2: 取引を発生させたトリガー種別（定時サイクル/価格変動/損切り機械執行）。
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
public sealed record TradeHistoryLine(
    int Index,
    TimeOnly Time,
    Market Market,
    string SymbolCode,
    string SymbolName,
    TradeSide Side,
    int Quantity,
    decimal FillPrice,
    decimal Cost,
    decimal Tax,
    decimal RealizedPnl,
    TradeTrigger Trigger,
    string RationaleSummary);

// FR-16, 04_report-templates 日報 §2「取引詳細（選定・売買の判断理由）」の 1 取引＝1 ブロック。文章は実 LLM ドラフト（後続）。
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
public sealed record SkippedDecision(TimeOnly Time, string Symbol, string Reason);

// FR-16, 04_report-templates 日報 §2 の入力（全明細＋取引詳細＋見送り判断）。実データ源（#63 台帳・実 LLM）連携は後続。
public sealed record TradeHistoryView
{
    public IReadOnlyList<TradeHistoryLine> Lines { get; init; } = [];

    public IReadOnlyList<TradeDetailBlock> Details { get; init; } = [];

    public IReadOnlyList<SkippedDecision> Skipped { get; init; } = [];
}
