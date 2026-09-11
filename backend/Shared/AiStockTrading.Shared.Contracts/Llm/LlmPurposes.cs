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

    /// <summary>
    /// FR-15, ADR-0033 決定5, IADR-0318 決定4: Stage 0 の**記録**（AI 判断の記録・再生方式）で発生した費用の計上区分。
    /// <para>
    /// 🔴 **これはゲートウェイへ送る用途キーではない。** 記録は本番と同じ用途（<see cref="TradeDecision"/>）で
    /// LLM を呼ぶ ——ADR-0011 が「検証したモデルと本番モデルの一致」を段階ゲートの前提としているため、
    /// モデル割当とフォールバック禁止の統制は本番と同一でなければならない。
    /// 一方 ADR-0033 決定5 は Stage 0 の費用を月次上限（15,000 円・取引判断サイクル対象）の**外**に置く。
    /// 用途キーは 1 つしかないため、**計上の境界（<c>ILlmUsageReporter</c>）で本キーへ付け替える**。
    /// </para>
    /// <para>
    /// 本キーは <see cref="IsTradeDecision"/> にも <see cref="IsReport"/> にも該当しないため、
    /// <c>LlmCostScope.IsGoverned</c> は偽になる（＝抑制動作を引き起こさない）。
    /// </para>
    /// </summary>
    public const string Stage0Recording = "stage0-recording";

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

    /// <summary>
    /// FR-06, FR-15, ADR-0033 決定5, ADR-0037 決定3, #750: Stage 0 の記録実行の計上区分か。
    /// <para>
    /// 🔴 <b>月報 §7 は本区分を「その他の用途」から分けて出す</b>——計画（04_report-templates 月報 §7）は
    /// 見積り承認額との対比を求めており、対比の分子が他の用途と混ざると超過が起きたのかを読めない。
    /// 分別の語彙を <see cref="IsTradeDecision"/> / <see cref="IsReport"/> と同じ場所に置くのは、
    /// 報告書側と費用統制側で判定がずれないようにするためである。
    /// </para>
    /// </summary>
    public static bool IsStage0Recording(string? purpose) =>
        string.Equals(purpose, Stage0Recording, StringComparison.OrdinalIgnoreCase);

    /// <summary>報告書生成の用途か（月報・週報・日報）。</summary>
    public static bool IsReport(string? purpose) =>
        string.Equals(purpose, ReportMonthly, StringComparison.OrdinalIgnoreCase)
        || string.Equals(purpose, ReportWeekly, StringComparison.OrdinalIgnoreCase)
        || string.Equals(purpose, ReportDaily, StringComparison.OrdinalIgnoreCase);
}
