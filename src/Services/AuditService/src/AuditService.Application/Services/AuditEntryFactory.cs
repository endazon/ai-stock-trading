using AiStockTrading.Audit.Application.State;
using AiStockTrading.Shared.Contracts.Events;

namespace AiStockTrading.Audit.Application.Services;

// FR-11, UC-07, IADR-0019: 各ドメインイベントを共通形 AuditEntry へ写像する純関数群。
// CorrelationId は注文系の DecisionId／市場系の EventId。Detail はイベント全量 JSON（AuditSerialization）。
public static class AuditEntryFactory
{
    // Summary の根拠テキスト（Rationale 等）は長くなり得るため上限で切り詰める。
    private const int SummaryMaxLength = 200;

    public static AuditEntry From(PriceMovementDetected e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(PriceMovementDetected), e.EventId, e.Symbol,
        $"{e.Symbol} 価格変動 {e.ChangeRatio:P2}（{e.BaselinePrice}→{e.Price}）",
        AuditSerialization.Serialize(e), e.DetectedAt, recordedAt);

    public static AuditEntry From(StopLossTriggered e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(StopLossTriggered), e.EventId, e.Symbol,
        $"{e.Symbol} 損切りライン到達 SL={e.StopLossPrice}（現在 {e.Price}・数量 {e.Quantity}）",
        AuditSerialization.Serialize(e), e.DetectedAt, recordedAt);

    public static AuditEntry From(TradeDecisionMade e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(TradeDecisionMade), e.DecisionId, e.Intent.Symbol,
        Truncate($"{e.Intent.Symbol} 判断 {e.Intent.Side}/{e.Intent.PositionEffect} 数量{e.Intent.Quantity}: {e.Rationale}"),
        AuditSerialization.Serialize(e), e.DecidedAt, recordedAt);

    public static AuditEntry From(OrderApproved e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(OrderApproved), e.DecisionId, e.Intent.Symbol,
        $"{e.Intent.Symbol} 承認 {e.Intent.Side}/{e.Intent.PositionEffect} 数量{e.ApprovedQuantity}",
        AuditSerialization.Serialize(e), e.ApprovedAt, recordedAt);

    public static AuditEntry From(OrderRejected e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(OrderRejected), e.DecisionId, e.Intent.Symbol,
        $"{e.Intent.Symbol} 拒否: {string.Join(",", e.Reasons)}",
        AuditSerialization.Serialize(e), e.RejectedAt, recordedAt);

    public static AuditEntry From(OrderExecuted e, Guid id, DateTimeOffset recordedAt) => new(
        // OrderExecuted は銘柄を持たない（DecisionId で相関して補完する系）。Symbol は null。
        id, nameof(OrderExecuted), e.DecisionId, Symbol: null,
        $"約定 {e.Status} 数量{e.FilledQuantity}@{e.AveragePrice}（OrderId={e.OrderId}）",
        AuditSerialization.Serialize(e), e.ExecutedAt, recordedAt);

    // FR-17: 全体前提条件の変更（設定管理 #19）。注文相関を持たないため "assumptions" の決定的 GUID を相関にする。
    public static AuditEntry From(AssumptionsChanged e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(AssumptionsChanged), AuditCorrelation.From("assumptions"), Symbol: null,
        Truncate($"前提条件 v{e.Version} 変更（{e.Actor}）: {e.Reason}"),
        AuditSerialization.Serialize(e), e.ChangedAt, recordedAt);

    // FR-07: 報告書の確定（報告書 #14）。同一 PeriodKey で同一相関になるよう "report:{PeriodKey}" の決定的 GUID を相関にする。
    public static AuditEntry From(ReportConfirmed e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(ReportConfirmed), AuditCorrelation.From($"report:{e.PeriodKey}"), Symbol: null,
        $"{e.Kind} 報告書 {e.PeriodKey} 確定（{e.Actor}・前提 v{e.AssumptionsVersion}）",
        AuditSerialization.Serialize(e), e.ConfirmedAt, recordedAt);

    private static string Truncate(string s) =>
        s.Length <= SummaryMaxLength ? s : s[..SummaryMaxLength] + "…";
}
