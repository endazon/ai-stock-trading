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
        var e = new StageTransitioned(3, 0, 1, "Promotion", "owner", "利用者承認による昇格", RecordedAt);

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
            new StageTransitioned(4, 1, 0, "Demotion", "owner", "利用者承認による差し戻し", RecordedAt), Guid.NewGuid(), RecordedAt);
        entry.CorrelationId.Should().Be(other.CorrelationId);
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
            new StageTransitioned(0, 0, 1, "Promotion", "owner", "x", RecordedAt), Guid.NewGuid(), RecordedAt);
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
}
