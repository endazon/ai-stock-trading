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

    // FR-05, FR-19, #154, IADR-0067: 注文の訂正（注文履歴テレメトリ）。相関は既存の注文系と同じ DecisionId。
    // OrderExecuted と同様に銘柄を持たない（DecisionId で相関して補完する系）ため Symbol は null。
    public static AuditEntry From(OrderModified e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(OrderModified), e.DecisionId, Symbol: null,
        Truncate($"訂正 数量{e.PreviousQuantity}→{e.Quantity} 価格{e.PreviousPrice}→{e.Price}（OrderId={e.OrderId}）: {e.Reason}"),
        AuditSerialization.Serialize(e), e.ModifiedAt, recordedAt);

    // FR-05, FR-19, #154, IADR-0067: 注文の取消（注文履歴テレメトリ）。相関は既存の注文系と同じ DecisionId。
    public static AuditEntry From(OrderCancelled e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(OrderCancelled), e.DecisionId, Symbol: null,
        Truncate($"取消（OrderId={e.OrderId}）: {e.Reason}"),
        AuditSerialization.Serialize(e), e.CancelledAt, recordedAt);

    // FR-17: 全体前提条件の変更（設定管理 #19）。注文相関を持たないため "assumptions" の決定的 GUID を相関にする。
    public static AuditEntry From(AssumptionsChanged e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(AssumptionsChanged), AuditCorrelation.From("assumptions"), Symbol: null,
        Truncate($"前提条件 v{e.Version} 変更（{e.Actor}）: {e.Reason}"),
        AuditSerialization.Serialize(e), e.ChangedAt, recordedAt);

    // FR-07: 報告書の確定（報告書 #14）。同一 PeriodKey で同一相関になるよう "report:{PeriodKey}" の決定的 GUID を相関にする。
    public static AuditEntry From(ReportConfirmed e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(ReportConfirmed), AuditCorrelation.From($"report:{e.PeriodKey}"), Symbol: null,
        Truncate($"{e.Kind} 報告書 {e.PeriodKey} 確定（{e.Actor}・前提 v{e.AssumptionsVersion}）"),
        AuditSerialization.Serialize(e), e.ConfirmedAt, recordedAt);

    // FR-06/07/09, IADR-0116, #280: 報告書ドラフトの提示（承認待ち）。確定（ReportConfirmed）と同じ "report:{PeriodKey}" 相関で
    // 束ね、監査照会で「いつ提示され、いつ確定したか」を 1 本の相関で辿れるようにする。
    public static AuditEntry From(ReportDraftPresented e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(ReportDraftPresented), AuditCorrelation.From($"report:{e.PeriodKey}"), Symbol: null,
        Truncate($"{e.Kind} 報告書 {e.PeriodKey} のドラフトを提示（版 {e.Version}・承認待ち）"),
        AuditSerialization.Serialize(e), e.OccurredAt, recordedAt);

    // NFR（費用）: 費用しきい値到達（費用統制 #23）。同一月×カテゴリで同一相関になるよう "cost:{Month}:{Category}" の決定的 GUID を相関にする。
    public static AuditEntry From(CostThresholdReached e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(CostThresholdReached), AuditCorrelation.From($"cost:{e.Month}:{e.Category}"), Symbol: null,
        $"費用しきい値到達 {e.Category} {e.Percent:P0}→{e.State}（{e.Month}）",
        AuditSerialization.Serialize(e), e.OccurredAt, recordedAt);

    // FR-01, FR-02: 情報収集の完了（情報収集 #9）。EventId を相関にする（市場系イベントと同様）。
    public static AuditEntry From(InformationCollected e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(InformationCollected), e.EventId, Symbol: null,
        $"情報収集完了 {e.ItemCount}件",
        AuditSerialization.Serialize(e), e.CollectedAt, recordedAt);

    // NFR（費用）, FR-04, IADR-0055: 実 LLM 呼び出しの費用発生（#79）。注文相関を持たないため、
    // 発生月で束ねられるよう "llm-cost:{yyyy-MM}" の決定的 GUID を相関にする（CostThresholdReached と同系）。
    public static AuditEntry From(LlmCostIncurred e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(LlmCostIncurred),
        AuditCorrelation.From($"llm-cost:{e.At.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)}"),
        Symbol: null,
        $"LLM 費用発生 {e.Amount:N2} 円",
        AuditSerialization.Serialize(e), e.At, recordedAt);

    // FR-20, FR-11, #167, IADR-0082: 段階ゲートの遷移（承認による昇格・差し戻し）。注文/市場相関を持たないため
    // "stage-gate" の決定的 GUID を相関にする（すべての段階遷移が同一相関で束ねられ、監査照会でまとめて辿れる）。
    public static AuditEntry From(StageTransitioned e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(StageTransitioned), AuditCorrelation.From("stage-gate"), Symbol: null,
        Truncate($"段階遷移 Stage {e.FromStage}→{e.ToStage}（{e.Kind}・{e.ApprovedBy}）: {e.Reason}"),
        AuditSerialization.Serialize(e), e.OccurredAt, recordedAt);

    // FR-20, FR-11, #166, IADR-0083: 撤退基準到達（自動安全側の発火）。段階遷移と同じ "stage-gate" 相関で束ね、
    // 監査照会で撤退と遷移をまとめて辿れるようにする（段階の実降格は提案に留まるため StageTransitioned は伴わない）。
    public static AuditEntry From(WithdrawalTriggered e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(WithdrawalTriggered), AuditCorrelation.From("stage-gate"), Symbol: null,
        Truncate($"撤退基準到達 → Stage {e.ProposedStage} 降格提案（{e.Reason}・{(e.HaltNewEntries ? "自動停止" : "提案のみ")}）"),
        AuditSerialization.Serialize(e), e.OccurredAt, recordedAt);

    // FR-20, FR-15, FR-11, #164, IADR-0089: バックテスト verdict（Stage 0 合格判定・#16）。段階ゲート系（Stage 0→1 解錠）の
    // ため段階遷移と同じ "stage-gate" 相関で束ね、監査照会でバックテスト供給と遷移をまとめて辿れるようにする。
    public static AuditEntry From(BacktestEvaluated e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(BacktestEvaluated), AuditCorrelation.From("stage-gate"), Symbol: null,
        Truncate($"バックテスト verdict: {(e.Passed ? "合格" : "不合格")}（最大DD {e.MaxDrawdownRatio:P2}・DSR {e.DeflatedSharpe:F2}）"
            + (e.Passed ? string.Empty : $" 未達: {e.FailedChecks}")),
        AuditSerialization.Serialize(e), e.EvaluatedAt, recordedAt);

    // UC-01, FR-09, FR-07, FR-11, #210: 日報未確定による取引スキップ。注文/市場相関を持たないため "daily-policy" の
    // 決定的 GUID を相関にする（日報未確定の見送りが同一相関で束ねられ、監査照会でまとめて辿れる）。
    public static AuditEntry From(DailyPolicyUnconfirmed e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(DailyPolicyUnconfirmed), AuditCorrelation.From("daily-policy"), Symbol: null,
        $"日報未確定により取引を見送り（営業日 {e.BusinessDay:yyyy-MM-dd}）",
        AuditSerialization.Serialize(e), e.OccurredAt, recordedAt);

    // FR-10, FR-11, UC-06, #292, IADR-0117: 利用者による建玉の手仕舞い要求。後続の OrderApproved / OrderExecuted と
    // 同一の DecisionId を相関に採り、「誰が・なぜ決済したか」から約定までを 1 本の相関で辿れるようにする。
    public static AuditEntry From(PositionCloseRequested e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(PositionCloseRequested), e.DecisionId, e.Symbol,
        Truncate($"{e.Symbol} 手仕舞い要求 {e.Side} 数量{e.Quantity}@{e.Price}（{e.Actor}）: {e.Reason}"),
        AuditSerialization.Serialize(e), e.RequestedAt, recordedAt);

    // FR-05, FR-10, FR-11, #292, IADR-0118: ブローカ実ポジションの観測。注文相関を持たないため "position-reconciliation" の
    // 決定的 GUID を相関にする（観測と乖離検知が同一相関で束ねられ、監査照会でまとめて辿れる）。
    public static AuditEntry From(BrokerPositionsObserved e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(BrokerPositionsObserved), AuditCorrelation.From("position-reconciliation"), Symbol: null,
        Truncate($"ブローカ建玉を観測 {e.Positions.Count}件"
            + (e.Positions.Count == 0 ? string.Empty : $": {string.Join(", ", e.Positions.Select(p => $"{p.Symbol}/{p.Market} {p.Quantity}"))}")),
        AuditSerialization.Serialize(e), e.ObservedAt, recordedAt);

    // FR-20, FR-05, FR-11, #385, IADR-0150: ブローカ稼働の観測（Stage 1 の営業日数の一次証跡）。
    // 注文相関を持たないため "stage1-uptime" の決定的 GUID を相関にする（稼働の観測どうしを 1 本の相関で辿れる）。
    public static AuditEntry From(BrokerAvailabilityObserved e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(BrokerAvailabilityObserved), AuditCorrelation.From("stage1-uptime"), Symbol: null,
        Truncate($"ブローカ稼働を観測（発注先 {e.Provider}・保証 {(int)e.CoveredInterval.TotalMinutes}分）"),
        AuditSerialization.Serialize(e), e.ObservedAt, recordedAt);

    // FR-05, FR-10, FR-11, #292, IADR-0118: 台帳とブローカの乖離検知（是正は伴わない）。観測と同一相関で束ねる。
    public static AuditEntry From(PositionReconciliationDrift e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(PositionReconciliationDrift), AuditCorrelation.From("position-reconciliation"), Symbol: null,
        Truncate($"建玉の乖離 {e.Drifts.Count}件: "
            + string.Join(", ", e.Drifts.Select(d => $"{d.Symbol}/{d.Market} 台帳{d.LedgerQuantity}≠ブローカ{d.BrokerQuantity}({d.Kind})"))),
        AuditSerialization.Serialize(e), e.DetectedAt, recordedAt);

    // FR-10, FR-11, UC-06, #330, IADR-0133 決定7: 維持率割れによる建玉の自動縮小。
    // **システムが自ら決済した唯一の統制**であり、この記録が「なぜ建玉が減ったか」の一次証跡になる。
    // 注文相関を持たない（1 回の発動で複数の決済注文を出す）ため "margin-reduction" の決定的 GUID を相関にし、
    // 発動どうしを 1 本の相関で辿れるようにする（BrokerPositionsObserved と同じ作法）。
    public static AuditEntry From(MaintenanceMarginReductionExecuted e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(MaintenanceMarginReductionExecuted), AuditCorrelation.From("margin-reduction"), Symbol: null,
        Truncate($"維持率割れの自動縮小 {Percent(e.RatioBefore)}→{FormatRatio(e.RatioAfter)}"
            + $"（閾値 {Percent(e.Threshold)}・回復目標 {Percent(e.RecoveryTarget)}）: "
            + string.Join(", ", e.Items.Select(i =>
                $"{i.Symbol}/{i.Market} {i.PositionSide} {i.Quantity}株 必要証拠金{i.RequiredMarginUsd}"))),
        AuditSerialization.Serialize(e), e.ExecutedAt, recordedAt);

    // 全量決済すると建玉が無くなり維持率の概念が消える（null）。「0%」と書くと破綻したように読めるため区別する。
    private static string FormatRatio(decimal? ratio) => ratio is { } r ? Percent(r) : "建玉なし";

    // 04_report-templates の <n%> 表記（小数第 1 位・文化非依存）。"P1" は文化により空白が入るため使わない。
    private static string Percent(decimal ratio) =>
        (ratio * 100m).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";

    private static string Truncate(string s) =>
        s.Length <= SummaryMaxLength ? s : s[..SummaryMaxLength] + "…";
}
