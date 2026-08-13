using AiStockTrading.Audit.Application.Services;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Audit.Application.Tests;

// FR-11, UC-07, IADR-0019: 各ドメインイベント→AuditEntry の写像（EventType・相関・銘柄・拒否理由）を検証する。
public class AuditEntryFactoryTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private static readonly DateTimeOffset RecordedAt = new(2026, 7, 10, 3, 0, 0, TimeSpan.Zero);

    private static OrderIntent Intent(PositionEffect effect = PositionEffect.Open) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 1_000m, effect);

    [Fact]
    public void TradeDecisionMade_は_DecisionId_相関で銘柄と根拠を記録する()
    {
        var decisionId = Guid.NewGuid();
        var e = new TradeDecisionMade(decisionId, Intent(), "上昇トレンドのため買い", new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero));

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("TradeDecisionMade");
        entry.CorrelationId.Should().Be(decisionId);
        entry.Symbol.Should().Be("AAPL");
        entry.Summary.Should().Contain("上昇トレンド");
        entry.OccurredAt.Should().Be(e.DecidedAt);
        entry.RecordedAt.Should().Be(RecordedAt);
        entry.Detail.Should().Contain("AAPL");
    }

    [Fact]
    public void OrderApproved_は_承認数量を要約に含める()
    {
        var decisionId = Guid.NewGuid();
        var e = new OrderApproved(decisionId, Intent(), 7, DateTimeOffset.UtcNow);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("OrderApproved");
        entry.CorrelationId.Should().Be(decisionId);
        entry.Summary.Should().Contain("7");
    }

    [Fact]
    public void OrderRejected_は_拒否理由を記録し照会できる()
    {
        var decisionId = Guid.NewGuid();
        var reasons = new[] { RejectionReason.KillSwitchActive, RejectionReason.DailyLossLimitReached };
        var e = new OrderRejected(decisionId, Intent(), reasons, DateTimeOffset.UtcNow);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("OrderRejected");
        entry.CorrelationId.Should().Be(decisionId);
        entry.Summary.Should().Contain(nameof(RejectionReason.KillSwitchActive));
        entry.Detail.Should().Contain(nameof(RejectionReason.DailyLossLimitReached)); // 列挙は文字列化
    }

    [Fact]
    public void OrderExecuted_は_銘柄なしで_DecisionId_相関を記録する()
    {
        var decisionId = Guid.NewGuid();
        var e = new OrderExecuted(decisionId, "ORD-1", OrderStatus.Filled, 10, 1_050m, DateTimeOffset.UtcNow, BrokerProvider.MoomooSimulate);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("OrderExecuted");
        entry.CorrelationId.Should().Be(decisionId);
        entry.Symbol.Should().BeNull();
        entry.Summary.Should().Contain("ORD-1");
    }

    [Fact]
    public void OrderModified_は_訂正前後の値つきで_DecisionId_相関を記録する()
    {
        // #154, IADR-0067: 注文履歴テレメトリ。訂正は既存の注文系と同じ DecisionId 相関に載せる。
        var decisionId = Guid.NewGuid();
        var e = new OrderModified(decisionId, "ORD-1", 10, 3_000m, 4, 2_950m, "数量縮小", DateTimeOffset.UtcNow);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("OrderModified");
        entry.CorrelationId.Should().Be(decisionId);
        entry.Symbol.Should().BeNull();
        entry.Summary.Should().Contain("ORD-1").And.Contain("数量縮小");
        // 監査台帳から「何がどう変わったか」が読めること（FR-11）。
        entry.Detail.Should().Contain("PreviousQuantity").And.Contain("PreviousPrice");
    }

    [Fact]
    public void OrderCancelled_は_理由つきで_DecisionId_相関を記録する()
    {
        // #154, IADR-0067: 注文履歴テレメトリ。取消も既存の注文系と同じ DecisionId 相関に載せる。
        var decisionId = Guid.NewGuid();
        var e = new OrderCancelled(decisionId, "ORD-1", "pause による強制取消", DateTimeOffset.UtcNow);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("OrderCancelled");
        entry.CorrelationId.Should().Be(decisionId);
        entry.Symbol.Should().BeNull();
        entry.Summary.Should().Contain("ORD-1").And.Contain("pause による強制取消");
    }

    [Fact]
    public void PriceMovementDetected_は_EventId_相関で銘柄を記録する()
    {
        var eventId = Guid.NewGuid();
        var e = new PriceMovementDetected(eventId, "7203", Market.Japan, 1_100m, 1_000m, 0.1m, DateTimeOffset.UtcNow);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("PriceMovementDetected");
        entry.CorrelationId.Should().Be(eventId);
        entry.Symbol.Should().Be("7203");
    }

    [Fact]
    public void AssumptionsChanged_は共通相関でバージョンとアクターを記録する()
    {
        var e = new AssumptionsChanged(3, "owner", "税率見直し", DateTimeOffset.UtcNow);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("AssumptionsChanged");
        entry.Summary.Should().Contain("v3");
        entry.Summary.Should().Contain("owner");
        // 同一「assumptions」キーは同一相関になる。
        entry.CorrelationId.Should().Be(AuditEntryFactory.From(
            new AssumptionsChanged(4, "owner", "別の変更", DateTimeOffset.UtcNow), Guid.NewGuid(), RecordedAt).CorrelationId);
    }

    [Fact]
    public void ReportConfirmed_は_PeriodKey_相関で確定者を記録する()
    {
        var e = new ReportConfirmed("daily-2026-07-10", "Daily", "owner", 2, DateTimeOffset.UtcNow);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("ReportConfirmed");
        entry.Summary.Should().Contain("daily-2026-07-10");
        entry.Summary.Should().Contain("owner");
        // 同一 PeriodKey は同一相関、別 PeriodKey は別相関。
        var same = AuditEntryFactory.From(new ReportConfirmed("daily-2026-07-10", "Daily", "u2", 3, DateTimeOffset.UtcNow), Guid.NewGuid(), RecordedAt);
        var other = AuditEntryFactory.From(new ReportConfirmed("daily-2026-07-11", "Daily", "u2", 3, DateTimeOffset.UtcNow), Guid.NewGuid(), RecordedAt);
        entry.CorrelationId.Should().Be(same.CorrelationId);
        entry.CorrelationId.Should().NotBe(other.CorrelationId);
    }

    [Fact]
    public void CostThresholdReached_は_月とカテゴリの相関でしきい値を記録する()
    {
        var e = new CostThresholdReached("2026-07", "Llm", 1.00m, "Halted", DateTimeOffset.UtcNow);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("CostThresholdReached");
        entry.Symbol.Should().BeNull();
        entry.Summary.Should().Contain("Halted");
        entry.Summary.Should().Contain("2026-07");
        entry.OccurredAt.Should().Be(e.OccurredAt);
        // 同一「月×カテゴリ」は同一相関、別カテゴリは別相関。
        var same = AuditEntryFactory.From(new CostThresholdReached("2026-07", "Llm", 0.80m, "Throttled", DateTimeOffset.UtcNow), Guid.NewGuid(), RecordedAt);
        var other = AuditEntryFactory.From(new CostThresholdReached("2026-07", "Infrastructure", 0.80m, "Throttled", DateTimeOffset.UtcNow), Guid.NewGuid(), RecordedAt);
        entry.CorrelationId.Should().Be(same.CorrelationId);
        entry.CorrelationId.Should().NotBe(other.CorrelationId);
    }

    [Fact]
    public void InformationCollected_は_EventId_相関で件数を記録する()
    {
        var eventId = Guid.NewGuid();
        var e = new InformationCollected(eventId, 5, DateTimeOffset.UtcNow);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("InformationCollected");
        entry.CorrelationId.Should().Be(eventId);
        entry.Symbol.Should().BeNull();
        entry.Summary.Should().Contain("5");
        entry.OccurredAt.Should().Be(e.CollectedAt);
    }

    [Fact]
    public void StopLossTriggered_は_EventId_相関で損切り情報を記録する()
    {
        var eventId = Guid.NewGuid();
        var e = new StopLossTriggered(eventId, "7203", Market.Japan, TradeSide.Buy, 5, 950m, 940m, DateTimeOffset.UtcNow);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("StopLossTriggered");
        entry.CorrelationId.Should().Be(eventId);
        entry.Symbol.Should().Be("7203");
        entry.Summary.Should().Contain("損切り");
    }

    [Fact]
    public void PositionCloseRequested_は操作者と理由を残し注文と同一相関になる()
    {
        // FR-10/FR-11, #292, IADR-0117: OrderApproved はアクターも理由も持たないため、これが唯一の「誰が・なぜ」の証跡。
        var decisionId = Guid.NewGuid();
        var e = new PositionCloseRequested(
            decisionId, "AAPL", Market.UnitedStates, TradeSide.Sell, 4072, 21m,
            "owner-1", "過大建玉の清算", RecordedAt);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("PositionCloseRequested");
        entry.Symbol.Should().Be("AAPL");
        entry.Summary.Should().Contain("owner-1").And.Contain("過大建玉の清算").And.Contain("4072");
        entry.OccurredAt.Should().Be(e.RequestedAt);
        entry.RecordedAt.Should().Be(RecordedAt);
        entry.Detail.Should().Contain("Actor");

        // 後続の承認・約定と同一 DecisionId 相関で束ねられ、要求から約定までを 1 本で辿れる。
        entry.CorrelationId.Should().Be(decisionId);
    }

    [Fact]
    public void StageTransitioned_は共通相関でfrom_to_承認者_種別を記録する()
    {
        // FR-20, FR-11, #167, IADR-0082: 段階遷移は注文/市場相関を持たないため "stage-gate" 共通相関に載せる。
        var e = new StageTransitioned(3, 0, 1, "Promotion", "owner", "利用者承認による昇格", RecordedAt, 100, false);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("StageTransitioned");
        entry.Symbol.Should().BeNull();
        // from/to・承認者・種別が一行で読める（FR-11）。
        entry.Summary.Should().Contain("0").And.Contain("1").And.Contain("owner").And.Contain("Promotion");
        entry.OccurredAt.Should().Be(e.OccurredAt);
        entry.RecordedAt.Should().Be(RecordedAt);
        // Detail からも承認者・種別が辿れる。
        entry.Detail.Should().Contain("ApprovedBy").And.Contain("Kind");
        // 段階遷移はすべて同一「stage-gate」相関で束ねられる（別遷移でも同一相関）。
        var other = AuditEntryFactory.From(
            new StageTransitioned(4, 1, 0, "Demotion", "owner", "利用者承認による差し戻し", RecordedAt, 100, false), Guid.NewGuid(), RecordedAt);
        entry.CorrelationId.Should().Be(other.CorrelationId);
    }

    // FR-20, FR-11, SC-02, #466, 06_daytrading-review §4.1 追補3（質問票 第15回 Q13-b）, IADR-0180:
    // **警告を無視して昇格した事実を記録に残す。** 設定変更の履歴には「下げた事実」が残るが、
    // **その設定で昇格した事実**は本イベント以外に残らない。
    [Fact]
    public void StageTransitioned_は警告を無視した昇格を要約とペイロードの両方へ残す()
    {
        var e = new StageTransitioned(
            5, 1, 2, "Promotion", "endazon", "利用者承認による昇格", RecordedAt,
            Stage1MinimumTradeCount: 5, Stage1BelowStatisticalBasis: true);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        // 要約を走査する監査で「なぜ 5 件で Stage 2 へ上がったのか」が目に入る。
        entry.Summary.Should().Contain("5 件").And.Contain("統計的根拠");
        // ペイロードにも 2 項目が残る（後から機械的に集計できる）。
        entry.Detail.Should().Contain("Stage1MinimumTradeCount").And.Contain("Stage1BelowStatisticalBasis");
    }

    // **否定形**: 既定値のままの遷移では要約に警告を足さない。
    // 常時添えると要約が長くなり、**警告そのものが埋もれる**。
    [Fact]
    public void StageTransitioned_は既定値のままなら要約に警告を足さない()
    {
        var e = new StageTransitioned(
            6, 0, 1, "Promotion", "endazon", "利用者承認による昇格", RecordedAt,
            Stage1MinimumTradeCount: 100, Stage1BelowStatisticalBasis: false);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.Summary.Should().NotContain("⚠").And.NotContain("統計的根拠");
        // 設定値そのものはペイロードに残る（警告が無くても「100 件だった」ことは追える）。
        entry.Detail.Should().Contain("Stage1MinimumTradeCount");
    }

    // 決定4: **設定値と警告有無を両方持つ。** 警告有無を設定値から後で導出しない——
    // 統計的根拠（100）が将来改訂されると、導出では**過去の記録の解釈が黙って書き換わる**。
    // 本テストは「同じ設定値でも警告有無が独立に記録され得る」ことで、両者が別の事実であることを固定する。
    [Fact]
    public void StageTransitioned_の警告有無は設定値から導出されていない()
    {
        var e = new StageTransitioned(
            7, 1, 2, "Promotion", "endazon", "利用者承認による昇格", RecordedAt,
            Stage1MinimumTradeCount: 100, Stage1BelowStatisticalBasis: true);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        // 件数 100 でも「当時警告が出ていた」と記録されていれば、そちらが記録として通る
        // （＝要約は設定値ではなく警告有無の項目を見ている）。
        entry.Summary.Should().Contain("統計的根拠");
    }

    [Fact]
    public void BacktestEvaluated_は段階ゲート相関でverdictと実DDを記録する()
    {
        // FR-20, FR-15, FR-11, #164, IADR-0089: バックテスト verdict は注文/市場相関を持たないため "stage-gate" 共通相関に載せる。
        var e = new BacktestEvaluated(
            Passed: false, MaxDrawdownRatio: 0.30m, DeflatedSharpe: 0.42,
            ProbabilityOfBacktestOverfitting: 0.7, FailedChecks: "DeflatedSharpe, MaxDrawdown", RecordedAt);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("BacktestEvaluated");
        entry.Symbol.Should().BeNull();
        // verdict・最大DD・未達条件が一行で読める（FR-11）。
        entry.Summary.Should().Contain("不合格").And.Contain("DeflatedSharpe");
        entry.OccurredAt.Should().Be(e.EvaluatedAt);
        entry.RecordedAt.Should().Be(RecordedAt);
        entry.Detail.Should().Contain("Passed").And.Contain("MaxDrawdownRatio");
        // 段階ゲート系（遷移・撤退・バックテスト）は同一「stage-gate」相関で束ねられる。
        var stage = AuditEntryFactory.From(
            new StageTransitioned(0, 0, 1, "Promotion", "owner", "x", RecordedAt, 100, false), Guid.NewGuid(), RecordedAt);
        entry.CorrelationId.Should().Be(stage.CorrelationId);
    }

    [Fact]
    public void DailyPolicyUnconfirmed_は共通相関で営業日を記録する()
    {
        // UC-01, FR-09, FR-07, FR-11, #210: 日報未確定による見送りは注文/市場相関を持たないため "daily-policy" 共通相関に載せる。
        var e = new DailyPolicyUnconfirmed(new DateOnly(2026, 7, 20), RecordedAt);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("DailyPolicyUnconfirmed");
        entry.Symbol.Should().BeNull();
        entry.Summary.Should().Contain("日報未確定").And.Contain("2026-07-20");
        entry.OccurredAt.Should().Be(e.OccurredAt);
        entry.RecordedAt.Should().Be(RecordedAt);
        entry.Detail.Should().Contain("BusinessDay");
        // 日報未確定の見送りはすべて同一「daily-policy」相関で束ねられる（別営業日でも同一相関）。
        var other = AuditEntryFactory.From(
            new DailyPolicyUnconfirmed(new DateOnly(2026, 7, 21), RecordedAt), Guid.NewGuid(), RecordedAt);
        entry.CorrelationId.Should().Be(other.CorrelationId);
    }

    [Fact]
    public void ReportDraftPresented_は確定と同じ報告書相関で提示を記録する()
    {
        // FR-06/07/09, FR-11, IADR-0116, #280: 提示と確定を同一相関で束ね、提示から確定までを監査照会で辿れるようにする。
        var e = new ReportDraftPresented("daily-2026-07-29", "Daily", "2026-07-29", "日報の要約", 2, RecordedAt);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("ReportDraftPresented");
        entry.Symbol.Should().BeNull();
        entry.Summary.Should().Contain("daily-2026-07-29").And.Contain("提示");
        entry.OccurredAt.Should().Be(e.OccurredAt);
        entry.RecordedAt.Should().Be(RecordedAt);
        entry.Detail.Should().Contain("PeriodKey");

        // 同一 PeriodKey の確定（ReportConfirmed）と同じ相関になる。
        var confirmed = AuditEntryFactory.From(
            new ReportConfirmed("daily-2026-07-29", "Daily", "owner", 1, RecordedAt), Guid.NewGuid(), RecordedAt);
        entry.CorrelationId.Should().Be(confirmed.CorrelationId);
    }

    [Fact]
    public void BrokerPositionsObserved_と_PositionReconciliationDrift_は同一相関で束ねられる()
    {
        // FR-05/FR-10/FR-11, #292, IADR-0118: 是正を伴わないため、この記録が乖離の唯一の永続証跡になる。
        // 観測と検知を同一相関に載せ、「いつ何を観測して、いつ乖離と判定したか」を 1 本で辿れるようにする。
        var observed = new BrokerPositionsObserved(
            [new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 4072, 20.5m)], RecordedAt);
        var drift = new PositionReconciliationDrift(
            [new PositionDriftItem("AAPL", Market.UnitedStates, 0, 4072, PositionDriftKind.BrokerOnly)],
            RecordedAt, RecordedAt);

        var observedEntry = AuditEntryFactory.From(observed, Id, RecordedAt);
        var driftEntry = AuditEntryFactory.From(drift, Guid.NewGuid(), RecordedAt);

        observedEntry.EventType.Should().Be("BrokerPositionsObserved");
        observedEntry.Summary.Should().Contain("AAPL").And.Contain("4072");
        observedEntry.OccurredAt.Should().Be(observed.ObservedAt);

        driftEntry.EventType.Should().Be("PositionReconciliationDrift");
        driftEntry.Summary.Should().Contain("AAPL").And.Contain("4072").And.Contain("BrokerOnly");
        driftEntry.OccurredAt.Should().Be(drift.DetectedAt);
        driftEntry.Detail.Should().Contain("Drifts");

        driftEntry.CorrelationId.Should().Be(observedEntry.CorrelationId);
    }
    // FR-10, FR-11, UC-06, #330, IADR-0133 決定7: 維持率割れの自動縮小（**記録先 1: 監査ログ**）。
    // 利用者の承認も AI も介在しない自動決済であるため、この記録が「なぜ建玉が減ったか」の一次証跡になる。
    [Fact]
    public void MaintenanceMarginReductionExecuted_は決済前後の維持率と決済建玉を要約に残す()
    {
        var e = new MaintenanceMarginReductionExecuted(
            Guid.NewGuid(), RatioBefore: 0.40m, Threshold: 0.40m, RecoveryTarget: 0.45m, RatioAfter: 0.4504m,
            [new MaintenanceMarginReductionItem(
                "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.ShortSell, 112, 100m, 3_360m)],
            RecordedAt);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("MaintenanceMarginReductionExecuted");
        entry.Summary.Should().Contain("AAPL").And.Contain("112").And.Contain("3360");
        entry.Summary.Should().Contain("40.0%").And.Contain("45.0%");
        entry.OccurredAt.Should().Be(e.ExecutedAt);
        // 明細（必要証拠金・数量）を含む全量 JSON が残る＝規則どおりの作動を事後に検証できる。
        entry.Detail.Should().Contain("RequiredMarginUsd").And.Contain("RecoveryTarget");
    }

    // 全量決済すると建玉が無くなり維持率の概念が消える。「0%」と書くと破綻したように読めるため区別する。
    [Fact]
    public void MaintenanceMarginReductionExecuted_は全量決済後の維持率を建玉なしと記す()
    {
        var e = new MaintenanceMarginReductionExecuted(
            Guid.NewGuid(), 0.20m, 0.40m, 0.45m, RatioAfter: null,
            [new MaintenanceMarginReductionItem(
                "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.ShortSell, 1_000, 100m, 30_000m)],
            RecordedAt);

        AuditEntryFactory.From(e, Id, RecordedAt).Summary.Should().Contain("建玉なし");
    }


    // T-10-243: FR-10, FR-11, UC-06, ADR-0016 決定4（2026-08-06 改訂）, #419, IADR-0159 ——
    // 強制買戻しの**推定**（記録先 1: 監査ログ）。**推定である以上、後から人が検証できなければならない**。
    // 要約に「推定」であることと突合に用いた 3 つの数量を、本文に根拠の全量（突合した自らの決済約定）を残す。
    [Fact]
    public void BuyInInferred_は推定であることと突合の根拠を残す()
    {
        var e = new BuyInInferred(
            Id, "GME", Market.UnitedStates,
            LedgerShortQuantity: 100, BrokerShortQuantity: 20, InFlightCloseQuantity: 10,
            UnexplainedQuantity: 70, NewlyInferredQuantity: 70,
            CoveringFills: [new BuyInCoveringFill(TradeSide.Buy, 40, 30m, RecordedAt)],
            BanUntil: new DateOnly(2026, 9, 6), ObservedAt: RecordedAt, InferredAt: RecordedAt);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("BuyInInferred");
        entry.Symbol.Should().Be("GME");
        entry.Summary.Should().Contain("推定").And.Contain("検知ではない");
        entry.Summary.Should().Contain("100").And.Contain("20").And.Contain("10").And.Contain("70");
        entry.Summary.Should().Contain("2026-09-06");
        entry.OccurredAt.Should().Be(e.InferredAt);
        // 根拠の全量（突合した自らの決済約定）が本文に残る＝推定の妥当性を事後に検証できる。
        entry.Detail.Should().Contain("CoveringFills").And.Contain("InFlightCloseQuantity");
    }
    // T-19-317: FR-19, FR-10, FR-11, UC-06, #425, ADR-0025 決定2, IADR-0165 ——
    // **GFV 発生回数の自前計数**を監査台帳へ残す（ADR-0025 が手入力を採らなかった理由の 1 つが
    // 「監査証跡に乗らない」ことであった）。
    //
    // **要約は「自前計数」「ガードの失敗」であることを明記しなければならない。** ブローカーの GFV 判定と
    // 一致する保証は無く、取り違えると「監査ログが 0 件だからブローカー側も 0 件だ」と読まれる。
    [Fact]
    public void GoodFaithViolationRecorded_は自前計数でありブローカー判定と一致しないことを明記する()
    {
        var decisionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var e = new GoodFaithViolationRecorded(
            Id, decisionId, "ORD-9", "AAPL", Market.UnitedStates,
            PurchaseAmountInBase: 1_234m, SettledCashInBase: null,
            OccurredOn: new DateOnly(2026, 8, 7), ExecutedAt: RecordedAt, RecordedAt: RecordedAt);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("GoodFaithViolationRecorded");
        entry.Symbol.Should().Be("AAPL");
        // 注文相関で束ねる（発注審査の記録と突き合わせて「なぜ拒否しなかったのか」を再構成できる）。
        entry.CorrelationId.Should().Be(decisionId);
        entry.Summary.Should().Contain("自前計数").And.Contain("ガードの失敗");
        entry.Summary.Should().Contain("ブローカーの GFV 判定とは一致しない");
        entry.Summary.Should().Contain("ORD-9").And.Contain("1234");
        // **決済済み資金は「未供給」と書く。** 0 と書くと「残高が 0 だった」と読まれる（#424 の表示規約）。
        entry.Summary.Should().Contain("未供給");
        entry.OccurredAt.Should().Be(e.RecordedAt);
        entry.Detail.Should().Contain("PurchaseAmountInBase").And.Contain("OccurredOn");
    }

    // FR-19, FR-11, UC-06, #464, ADR-0028 決定2, IADR-0182:
    // 解除は「誰が・いつ・どの記録に対して」の粒度で監査へ残る。
    [Fact]
    public void GoodFaithViolationsCleared_は誰がどの記録を解除したかを残す()
    {
        var e = new GoodFaithViolationsCleared(
            "endazon", "決済済み資金の判定を修正した", ["ord-1", "ord-2"], RemainingCount: 0, RecordedAt);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("GoodFaithViolationsCleared");
        entry.Summary.Should().Contain("endazon").And.Contain("2 件");
        // 🔴 **解けたのは停止であって記録ではない。** 監査を読む者が「記録が消えた」と誤読しないよう明示する。
        entry.Summary.Should().Contain("失効しません");
        // どの記録に対して解除したかは payload に残る（決定2 の「どの記録に対して」）。
        entry.Detail.Should().Contain("ord-1").And.Contain("ord-2");
        entry.OccurredAt.Should().Be(e.ClearedAt);
    }

    // **残件数を載せる。** 解除の最中に新たな違反が計上され得るため 0 とは限らず、
    // 0 でなければ停止は続いている——「解除したのに止まったまま」を監査から説明できるようにする。
    [Fact]
    public void GoodFaithViolationsCleared_は解除後の残件数を残す()
    {
        var e = new GoodFaithViolationsCleared(
            "endazon", "是正済み", ["ord-1"], RemainingCount: 1, RecordedAt);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.Summary.Should().Contain("残 1 件");
        entry.Detail.Should().Contain("RemainingCount");
    }
}
