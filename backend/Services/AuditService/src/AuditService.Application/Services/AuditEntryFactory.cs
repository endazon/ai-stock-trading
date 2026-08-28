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
        $"LLM 費用発生 {e.Amount:N2} 円（用途 {e.Purpose ?? "不明"}・モデル {e.Model ?? "不明"}）",
        AuditSerialization.Serialize(e), e.At, recordedAt);

    // FR-04, FR-06, FR-11, ADR-0017 決定4-(3), #335, IADR-0217: フォールバック発火（用途別・原因別）。
    // 月報の「当月のフォールバック発火回数」の供給元であるため、**発生月で束ねられる相関**にする。
    public static AuditEntry From(LlmFallbackFired e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(LlmFallbackFired),
        AuditCorrelation.From($"llm-fallback:{e.OccurredAt.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)}"),
        Symbol: null,
        Truncate($"LLM 割当逸脱（{e.Outcome}）用途 {e.Purpose}: 期待 {e.ExpectedModel ?? "なし"} → 実際 {e.EffectiveModel ?? "不明"}"),
        AuditSerialization.Serialize(e), e.OccurredAt, recordedAt);

    // FR-04, FR-11, UC-01, ADR-0017 決定2, #335, IADR-0216: 割当モデル不可による取引判断の見送り。
    // **障害ではなく設計上の正常な結果**であり、日報の「当日のスキップ回数」の供給元になる。
    // 発生日で辿れるよう、発火と同じく月で束ねる相関にする（日報は日で絞って数える）。
    public static AuditEntry From(TradeDecisionSkipped e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(TradeDecisionSkipped),
        AuditCorrelation.From($"trade-decision-skip:{e.OccurredAt.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)}"),
        Symbol: null,
        Truncate($"取引判断の見送り（{e.Reason}）用途 {e.Purpose}: 期待 {e.ExpectedModel ?? "なし"} → 実際 {e.EffectiveModel ?? "不明"}"),
        AuditSerialization.Serialize(e), e.OccurredAt, recordedAt);

    // FR-20, FR-11, #167, IADR-0082: 段階ゲートの遷移（承認による昇格・差し戻し）。注文/市場相関を持たないため
    // "stage-gate" の決定的 GUID を相関にする（すべての段階遷移が同一相関で束ねられ、監査照会でまとめて辿れる）。
    //
    // FR-11, #466, §4.1 追補3（質問票 第15回 Q13-b）, IADR-0180: **警告を無視して昇格した事実**を要約にも出す。
    // payload（`AuditSerialization.Serialize`）には 2 項目が自動で載るが、**要約を走査する監査**では
    // 「なぜ 60 営業日・5 件で Stage 2 へ上がったのか」が目に入らない。警告が出ていた遷移にだけ添える
    // （常時添えると要約が長くなり、警告そのものが埋もれる）。
    public static AuditEntry From(StageTransitioned e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(StageTransitioned), AuditCorrelation.From("stage-gate"), Symbol: null,
        Truncate($"段階遷移 Stage {e.FromStage}→{e.ToStage}（{e.Kind}・{e.ApprovedBy}）: {e.Reason}"
            + (e.Stage1BelowStatisticalBasis
                ? $"（⚠ 最小取引件数 {e.Stage1MinimumTradeCount} 件・統計的根拠を満たさない設定のまま遷移）"
                : string.Empty)),
        AuditSerialization.Serialize(e), e.OccurredAt, recordedAt);

    // FR-19, FR-10, FR-11, #464, ADR-0028 決定2, IADR-0182: GFV 違反による停止の**解除**。
    //
    // ADR-0028 決定2 は「**誰が・いつ・どの記録に対して**解除したか」を求める。要約に解除者・件数・
    // 残件数を出し、payload に解除した記録の一覧を残す。
    //
    // 🔴 **要約に「解除しました」だけを書かない。** 決定1 が「違反記録は失効させない」と定めており、
    // 解けたのは**停止**であって記録ではない。監査を読む者が「記録が消えた」と誤読しないよう明示する。
    // GFV 系は注文相関を持たない統制操作のため "good-faith-violation" の決定的 GUID で束ねる。
    public static AuditEntry From(GoodFaithViolationsCleared e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(GoodFaithViolationsCleared), AuditCorrelation.From("good-faith-violation"), Symbol: null,
        Truncate($"GFV 違反による停止を解除 {e.ClearedOrderIds.Count} 件（{e.ClearedBy}・残 {e.RemainingCount} 件）"
            + $"—— **違反記録そのものは失効しません**: {e.Reason}"),
        AuditSerialization.Serialize(e), e.ClearedAt, recordedAt);

    // FR-10, FR-11, SC-03, #465, ADR-0027 決定1, IADR-0183: 借株料の**日次の計上額**。
    //
    // ADR-0027 決定1 は「**日次の計上額は監査ログへ残す**」と定める ——
    // **累計だけを保持すると、後から日別の内訳を復元できない。**
    // 建玉（銘柄）ごとに追えるよう相関を銘柄で束ね、`Symbol` にも載せる。
    public static AuditEntry From(BorrowFeeAccrued e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(BorrowFeeAccrued), AuditCorrelation.From($"borrow-fee:{e.Symbol}:{e.Market}"), e.Symbol,
        Truncate($"借株料を計上 {e.TradingDay:yyyy-MM-dd} {e.Symbol}（{e.Market}）: {e.AmountUsd} USD"
            + $"（計上日の年率 {e.RateAnnual}・建玉評価額 {e.PositionValueUsd} USD）"),
        AuditSerialization.Serialize(e), e.AccruedAt, recordedAt);

    // FR-10, FR-11, SC-03, #465, ADR-0027 決定4, IADR-0183: 借株料を**計上できなかった日**。
    //
    // 🔴 **「0 円」と書かない。** 決定4 は「取得できなかった日を 0 として計上しない」と明示している ——
    // 0 と書けば「その日は費用が発生しなかった」と読め、**Stage 1 の「借株料は 1 円も掛かっていない」という
    // 誤読が構造的に起こる**。要約は「取得できず未計上」であることを明言する。
    public static AuditEntry From(BorrowFeeAccrualUnavailable e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(BorrowFeeAccrualUnavailable), AuditCorrelation.From($"borrow-fee:{e.Symbol}:{e.Market}"), e.Symbol,
        Truncate($"借株料の料率を取得できず未計上 {e.TradingDay:yyyy-MM-dd} {e.Symbol}（{e.Market}）"
            + $"—— **0 円ではありません（費用は発生しています）**: {e.Reason}"),
        AuditSerialization.Serialize(e), e.ObservedAt, recordedAt);

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

    // FR-19, FR-10, FR-11, #375, ADR-0021 決定3, IADR-0153: 口座種別の観測。
    // 注文相関を持たないため "broker-account-type" の決定的 GUID を相関にする（観測どうしを 1 本の相関で辿れる）。
    // **要約に決済済み資金・GFV 回数の「未供給」を明示する**——供給が無いことが現金口座の買付を止める理由であり、
    // 事後に「なぜ止まっていたのか」を要約だけで辿れる必要がある。
    public static AuditEntry From(BrokerAccountObserved e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(BrokerAccountObserved), AuditCorrelation.From("broker-account-type"), Symbol: null,
        // #425, ADR-0025 決定2: GFV 発生回数は本観測に含まれない（ブローカーが供給できない）。
        // 自前計数は GoodFaithViolationRecorded として別に記録される。
        Truncate($"口座種別を観測（発注先 {e.Provider}・種別 {e.Account.AccountType}"
            + $"・決済済み資金 {Describe(e.Account.SettledCashInBase)}）"),
        AuditSerialization.Serialize(e), e.ObservedAt, recordedAt);

    private static string Describe<T>(T? value) where T : struct =>
        value?.ToString() ?? "未供給";

    // FR-19, FR-10, FR-11, UC-06, #425, ADR-0025 決定2, IADR-0165: 未決済資金による買付（GFV 発生）の**自前計数**。
    //
    // ★ 要約に「自前計数」「ガードの失敗」であることを明記する。**ブローカーが GFV と判定した件数の写しではなく、
    //   両者が一致する保証はない**（ADR-0025 §理由）。取り違えると、口座制限（3 回で 90 日）の予防という
    //   目的が崩れる——「監査ログに 0 件だからブローカー側も 0 件だ」とは読めない。
    //
    // 注文相関（DecisionId）を相関に用いる。発注審査の記録（OrderApproved / OrderRejected）と同じ相関で束ね、
    // 「なぜ発注前のガードが拒否しなかったのか」を事後に再構成できるようにする。
    public static AuditEntry From(GoodFaithViolationRecorded e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(GoodFaithViolationRecorded), e.DecisionId, e.Symbol,
        Truncate($"未決済資金による買付を GFV 発生として自前計数（自らのガードの失敗であり、"
            + $"ブローカーの GFV 判定とは一致しない）: {e.Symbol}/{e.Market} 注文 {e.OrderId}"
            + $"・買付額 {e.PurchaseAmountInBase}・決済済み資金 {Describe(e.SettledCashInBase)}"
            + $"・計上日 {e.OccurredOn:yyyy-MM-dd}"),
        AuditSerialization.Serialize(e), e.RecordedAt, recordedAt);

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

    // FR-10, FR-11, UC-06, ADR-0016 決定4（2026-08-06 改訂）, #419, IADR-0159:
    // 強制買戻し（buy-in）の**事後推定**。**推定である以上、後から人が検証できなければならない**（#419 の設計上の注意）——
    // 要約に「推定」であることと突合に用いた 3 つの数量（台帳・ブローカ・処理中の決済）を明記し、
    // 根拠の全体（突合した自らの決済約定）は本文（シリアライズしたイベント）に残す。
    // 注文相関を持たないため "buy-in-inference" の決定的 GUID を相関にし、推定どうしを 1 本の相関で辿れるようにする。
    public static AuditEntry From(BuyInInferred e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(BuyInInferred), AuditCorrelation.From("buy-in-inference"), e.Symbol,
        Truncate($"強制買戻しと推定（検知ではない）: {e.Symbol}/{e.Market} 台帳{e.LedgerShortQuantity}株"
            + $"・ブローカ{e.BrokerShortQuantity}株・処理中の決済{e.InFlightCloseQuantity}株"
            + $"→ 説明できない消失{e.NewlyInferredQuantity}株（累計{e.UnexplainedQuantity}株）。"
            + $"{e.BanUntil:yyyy-MM-dd} まで空売り禁止。突合した決済約定 {e.CoveringFills.Count}件"),
        AuditSerialization.Serialize(e), e.InferredAt, recordedAt);

    // FR-10, FR-17, FR-11, #381, ADR-0022 決定2, IADR-0196: 為替の情報源がフォールバックへ落ちた。
    //
    // 注文相関を持たないため決定的 GUID を相関にする。**落ちた／戻ったを同じ相関に置く**ことで、
    // 台帳から**期間を 1 本の相関で辿れる**（ADR-0022 決定2 が求める「切り替わっていた期間」）。
    //
    // 🔴 **相関は通貨ごとに分ける。** USD と EUR は独立して劣化し得る（FxSourceStatusTracker は
    // 通貨ごとに状態を持つ）。固定の相関にすると**複数通貨の切替・復帰が 1 本に混在し、
    // 「期間を 1 本の相関で辿れる」が成立しなくなる**（AI レビューの指摘・2026-08-15）。
    // 並行するタイムラインをエンティティで分ける形は BorrowFeeAccrued の
    // `borrow-fee:{Symbol}:{Market}` と同じである。
    // Subject は通貨コード（銘柄ではない）——為替の劣化は銘柄単位ではなく通貨単位で起きる。
    public static AuditEntry From(FxRateSourceFellBack e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(FxRateSourceFellBack), AuditCorrelation.From($"fx-rate-source:{e.Quote}"), e.Quote,
        $"為替レート源がフォールバックへ切替: {e.Quote} は {e.SourceName}（優先度 {e.Rank}/{e.TotalSources}）から取得。"
            + "第一の源が使えていない（鮮度が週次へ悪化し得る）。新規建ては止まっていない",
        AuditSerialization.Serialize(e), e.OccurredAt, recordedAt);

    // FR-06, FR-10, FR-11, #513, ADR-0022 決定1, IADR-0225: どの情報源から取ったかの記録（暦日ごとに 1 件）。
    //
    // 🔴 **これが「静かな期間」の出典の唯一の根拠である。** 遷移でしか発行しない設計（IADR-0196 決定1）では
    // 切替も復帰も起きない期間に何も残らず、**日報の出典が平常時こそ「特定できません」になっていた**。
    // 相関は切替・復帰と**同じ `fx-rate-source:{Quote}`** に置く——通貨ごとの 1 本のタイムラインに
    // 「使った・落ちた・戻った」が時系列で並ぶ（別相関にすると期間の追跡が 2 本に割れる）。
    public static AuditEntry From(FxRateSourceUsed e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(FxRateSourceUsed), AuditCorrelation.From($"fx-rate-source:{e.Quote}"), e.Quote,
        $"為替レート源の使用記録（当日初回）: {e.Quote} は {e.SourceName}"
            + $"（優先度 {e.Rank}/{e.TotalSources}{(e.IsPrimary ? "・第一の情報源" : "・フォールバック")}）から取得",
        AuditSerialization.Serialize(e), e.OccurredAt, recordedAt);

    // 🔴 期間は**このイベント自身が持つ**（受け手に引き算させない）。片方を取りこぼしても期間が黙って狂わない。
    public static AuditEntry From(FxRateSourcePrimaryRestored e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(FxRateSourcePrimaryRestored), AuditCorrelation.From($"fx-rate-source:{e.Quote}"), e.Quote,
        $"為替レート源が第一（{e.SourceName}）へ復帰: {e.Quote}。"
            + $"フォールバックしていた期間 {FormatDuration(e.FallbackDuration)}（{e.FellBackAt:yyyy-MM-dd HH:mm}Z 〜）",
        AuditSerialization.Serialize(e), e.OccurredAt, recordedAt);

    // 🔴 **「停止」ではない。** 警告域は値を返して続行する（ADR-0022 決定5）。要約でも明示する——
    // 台帳を読む人が「止まっていた」と誤読すると、事後の検証が事実とずれる。
    public static AuditEntry From(FxRateStale e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(FxRateStale), AuditCorrelation.From($"fx-rate-source:{e.Quote}"), e.Quote,
        $"為替レートの鮮度警告: {e.Quote} 観測日 {e.AsOf:yyyy-MM-dd}・経過 {e.AgeDays:0.#} 日"
            + $"（警告 {e.WarnThresholdDays:0.#} 日 / 停止 {e.MaxAgeDays:0.#} 日）。"
            + "直近レートで続行しており新規建ては止まっていない",
        AuditSerialization.Serialize(e), e.OccurredAt, recordedAt);

    // FR-10, FR-11, #381, IADR-0198: 鮮度切れのレートで決済した。**取引 1 件ごとに残す**（抑止しない）。
    //
    // 相関は銘柄ごとに分ける——**同じ銘柄の決済を時系列で辿れる**ようにするため。
    // 🔴 要約に**観測日**を出す。台帳の行には観測日の列が無く、**ここが唯一の手掛かり**である。
    public static AuditEntry From(PositionClosedWithStaleFxRate e, Guid id, DateTimeOffset recordedAt) => new(
        id, nameof(PositionClosedWithStaleFxRate), AuditCorrelation.From($"fx-stale-close:{e.Symbol}"), e.Symbol,
        $"鮮度切れのレートで決済: {e.Symbol}/{e.Market} 数量{e.Quantity}・"
            + $"換算率 {e.FxRateToBase}（{e.Quote}→基準通貨）・**観測日 {e.RateAsOf:yyyy-MM-dd}（{e.AgeDays:0.#} 日前）**。"
            + "計画どおり手仕舞いは止めていないが、**換算額は実勢から乖離し得る**",
        AuditSerialization.Serialize(e), e.OccurredAt, recordedAt);

    // 期間は日・時間・分のうち意味のある単位まで。秒まで書くと読み手が桁を数えることになる。
    private static string FormatDuration(TimeSpan d) =>
        d.TotalDays >= 1 ? $"{d.TotalDays:0.#} 日"
        : d.TotalHours >= 1 ? $"{d.TotalHours:0.#} 時間"
        : $"{d.TotalMinutes:0.#} 分";

    // 全量決済すると建玉が無くなり維持率の概念が消える（null）。「0%」と書くと破綻したように読めるため区別する。
    private static string FormatRatio(decimal? ratio) => ratio is { } r ? Percent(r) : "建玉なし";

    // 04_report-templates の <n%> 表記（小数第 1 位・文化非依存）。"P1" は文化により空白が入るため使わない。
    private static string Percent(decimal ratio) =>
        (ratio * 100m).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";

    private static string Truncate(string s) =>
        s.Length <= SummaryMaxLength ? s : s[..SummaryMaxLength] + "…";
}
