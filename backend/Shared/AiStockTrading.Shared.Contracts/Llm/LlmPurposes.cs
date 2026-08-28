namespace AiStockTrading.Shared.Contracts.Llm;

// FR-04, FR-06, ADR-0014, ADR-0017, #335: 基盤 LLM ゲートウェイへ送る用途（purpose）キーの単一情報源。
//
// ⚠️ ここの文字列は基盤（microservices-platform）の `Llm:Routing:PurposeModels` のキーと**一致していなければ
// ならない**。不一致だと `LlmRouter.ResolveModel` が未知 purpose として扱い、例外もログも出さずに
// `DefaultModel` へ落ちる＝**割当が無音で失効する**（platform IADR-0102 / IADR-0106 / IADR-0112 の罠）。
// 本システムはその落下を LlmAssignments の実効モデル検証で検知する側に立つ（IADR-0215）。
public static class LlmPurposes
{
    /// <summary>取引判断（二段判断の本判断）。ADR-0014 §決定1・ADR-0017 決定2 によりフォールバック禁止。</summary>
    public const string TradeDecision = "trade-decision";

    /// <summary>
    /// 取引判断（二段判断の一次スクリーニング）。01_architecture-overview の層別割当により
    /// 本判断と別のモデル（軽量）を充てる。**取引判断の一部であるためフォールバックは禁止**である。
    /// </summary>
    public const string TradeDecisionScreening = "trade-decision-screening";

    /// <summary>月報。ADR-0015 により第 1 候補は ZDR 対応モデルへ改定された。</summary>
    public const string ReportMonthly = "report-monthly";

    /// <summary>週報。</summary>
    public const string ReportWeekly = "report-weekly";

    /// <summary>日報。</summary>
    public const string ReportDaily = "report-daily";

    /// <summary>取引判断系の用途か（本判断・スクリーニングの両方）。フォールバック禁止・費用上限の対象。</summary>
    public static bool IsTradeDecision(string? purpose) =>
        string.Equals(purpose, TradeDecision, StringComparison.OrdinalIgnoreCase)
        || string.Equals(purpose, TradeDecisionScreening, StringComparison.OrdinalIgnoreCase);

    /// <summary>報告書生成の用途か（月報・週報・日報）。</summary>
    public static bool IsReport(string? purpose) =>
        string.Equals(purpose, ReportMonthly, StringComparison.OrdinalIgnoreCase)
        || string.Equals(purpose, ReportWeekly, StringComparison.OrdinalIgnoreCase)
        || string.Equals(purpose, ReportDaily, StringComparison.OrdinalIgnoreCase);
}
