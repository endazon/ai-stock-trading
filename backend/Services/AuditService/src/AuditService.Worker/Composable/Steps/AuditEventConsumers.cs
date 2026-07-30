using AiStockTrading.Audit.Application.Ports;
using AiStockTrading.Audit.Application.Services;
using AiStockTrading.Shared.Contracts.Events;
using MassTransit;

namespace AiStockTrading.Audit.Worker.Composable.Steps;

// FR-11, UC-07, IADR-0019: 全ドメインイベントを購読して監査台帳へ記録する Consumer 群。
// 冪等キーは MassTransit MessageId（再送で重複記録しない）。記録時刻は IClock。写像は AuditEntryFactory（純関数）。
internal static class AuditConsumerHelper
{
    public static Guid MessageId<T>(ConsumeContext<T> context) where T : class =>
        context.MessageId ?? Guid.NewGuid();
}

internal sealed class PriceMovementDetectedAuditConsumer(IAuditEventStore store, IClock clock)
    : IConsumer<PriceMovementDetected>
{
    public Task Consume(ConsumeContext<PriceMovementDetected> context)
    {
        store.Append(AuditEntryFactory.From(context.Message, AuditConsumerHelper.MessageId(context), clock.UtcNow));
        return Task.CompletedTask;
    }
}

internal sealed class StopLossTriggeredAuditConsumer(IAuditEventStore store, IClock clock)
    : IConsumer<StopLossTriggered>
{
    public Task Consume(ConsumeContext<StopLossTriggered> context)
    {
        store.Append(AuditEntryFactory.From(context.Message, AuditConsumerHelper.MessageId(context), clock.UtcNow));
        return Task.CompletedTask;
    }
}

internal sealed class TradeDecisionMadeAuditConsumer(IAuditEventStore store, IClock clock)
    : IConsumer<TradeDecisionMade>
{
    public Task Consume(ConsumeContext<TradeDecisionMade> context)
    {
        store.Append(AuditEntryFactory.From(context.Message, AuditConsumerHelper.MessageId(context), clock.UtcNow));
        return Task.CompletedTask;
    }
}

internal sealed class OrderApprovedAuditConsumer(IAuditEventStore store, IClock clock)
    : IConsumer<OrderApproved>
{
    public Task Consume(ConsumeContext<OrderApproved> context)
    {
        store.Append(AuditEntryFactory.From(context.Message, AuditConsumerHelper.MessageId(context), clock.UtcNow));
        return Task.CompletedTask;
    }
}

internal sealed class OrderRejectedAuditConsumer(IAuditEventStore store, IClock clock)
    : IConsumer<OrderRejected>
{
    public Task Consume(ConsumeContext<OrderRejected> context)
    {
        store.Append(AuditEntryFactory.From(context.Message, AuditConsumerHelper.MessageId(context), clock.UtcNow));
        return Task.CompletedTask;
    }
}

internal sealed class OrderExecutedAuditConsumer(IAuditEventStore store, IClock clock)
    : IConsumer<OrderExecuted>
{
    public Task Consume(ConsumeContext<OrderExecuted> context)
    {
        store.Append(AuditEntryFactory.From(context.Message, AuditConsumerHelper.MessageId(context), clock.UtcNow));
        return Task.CompletedTask;
    }
}

// FR-05, FR-19, #154, IADR-0067: 注文の訂正（注文履歴テレメトリ）を監査台帳へ記録する（FR-11: 全イベントの時系列記録）。
internal sealed class OrderModifiedAuditConsumer(IAuditEventStore store, IClock clock)
    : IConsumer<OrderModified>
{
    public Task Consume(ConsumeContext<OrderModified> context)
    {
        store.Append(AuditEntryFactory.From(context.Message, AuditConsumerHelper.MessageId(context), clock.UtcNow));
        return Task.CompletedTask;
    }
}

// FR-05, FR-19, #154, IADR-0067: 注文の取消（注文履歴テレメトリ）を監査台帳へ記録する。
internal sealed class OrderCancelledAuditConsumer(IAuditEventStore store, IClock clock)
    : IConsumer<OrderCancelled>
{
    public Task Consume(ConsumeContext<OrderCancelled> context)
    {
        store.Append(AuditEntryFactory.From(context.Message, AuditConsumerHelper.MessageId(context), clock.UtcNow));
        return Task.CompletedTask;
    }
}

// FR-17: 全体前提条件の変更（設定管理 #19）を監査台帳へ記録する。
internal sealed class AssumptionsChangedAuditConsumer(IAuditEventStore store, IClock clock)
    : IConsumer<AssumptionsChanged>
{
    public Task Consume(ConsumeContext<AssumptionsChanged> context)
    {
        store.Append(AuditEntryFactory.From(context.Message, AuditConsumerHelper.MessageId(context), clock.UtcNow));
        return Task.CompletedTask;
    }
}

// FR-07: 報告書の確定（報告書 #14）を監査台帳へ記録する。
internal sealed class ReportConfirmedAuditConsumer(IAuditEventStore store, IClock clock)
    : IConsumer<ReportConfirmed>
{
    public Task Consume(ConsumeContext<ReportConfirmed> context)
    {
        store.Append(AuditEntryFactory.From(context.Message, AuditConsumerHelper.MessageId(context), clock.UtcNow));
        return Task.CompletedTask;
    }
}

// FR-06/07/09, FR-11, IADR-0116, #280: 報告書ドラフトの提示（自動生成スケジューラ）を監査台帳へ記録する。
// 確定（ReportConfirmed）と同じ相関で束ねられ、提示から確定までのリードタイムを監査照会で辿れる。
internal sealed class ReportDraftPresentedAuditConsumer(IAuditEventStore store, IClock clock)
    : IConsumer<ReportDraftPresented>
{
    public Task Consume(ConsumeContext<ReportDraftPresented> context)
    {
        store.Append(AuditEntryFactory.From(context.Message, AuditConsumerHelper.MessageId(context), clock.UtcNow));
        return Task.CompletedTask;
    }
}

// NFR（費用）: 費用しきい値到達（費用統制 #23）を監査台帳へ記録する。
internal sealed class CostThresholdReachedAuditConsumer(IAuditEventStore store, IClock clock)
    : IConsumer<CostThresholdReached>
{
    public Task Consume(ConsumeContext<CostThresholdReached> context)
    {
        store.Append(AuditEntryFactory.From(context.Message, AuditConsumerHelper.MessageId(context), clock.UtcNow));
        return Task.CompletedTask;
    }
}

// NFR（費用）, FR-04, IADR-0055: 実 LLM 費用の発生（#79）も監査台帳へ記録する（FR-11: 全イベントの時系列記録）。
internal sealed class LlmCostIncurredAuditConsumer(IAuditEventStore store, IClock clock)
    : IConsumer<LlmCostIncurred>
{
    public Task Consume(ConsumeContext<LlmCostIncurred> context)
    {
        store.Append(AuditEntryFactory.From(context.Message, AuditConsumerHelper.MessageId(context), clock.UtcNow));
        return Task.CompletedTask;
    }
}

// FR-01, FR-02: 情報収集の完了（情報収集 #9）を監査台帳へ記録する。
internal sealed class InformationCollectedAuditConsumer(IAuditEventStore store, IClock clock)
    : IConsumer<InformationCollected>
{
    public Task Consume(ConsumeContext<InformationCollected> context)
    {
        store.Append(AuditEntryFactory.From(context.Message, AuditConsumerHelper.MessageId(context), clock.UtcNow));
        return Task.CompletedTask;
    }
}

// FR-20, #167, IADR-0082: 段階ゲートの遷移（承認による昇格・差し戻し #20/#167）を中央監査台帳へ記録する。
internal sealed class StageTransitionedAuditConsumer(IAuditEventStore store, IClock clock)
    : IConsumer<StageTransitioned>
{
    public Task Consume(ConsumeContext<StageTransitioned> context)
    {
        store.Append(AuditEntryFactory.From(context.Message, AuditConsumerHelper.MessageId(context), clock.UtcNow));
        return Task.CompletedTask;
    }
}

// FR-20, #166, IADR-0083: 撤退基準到達（自動安全側の発火・撤退の定期評価ドライバ #166）を中央監査台帳へ記録する。
internal sealed class WithdrawalTriggeredAuditConsumer(IAuditEventStore store, IClock clock)
    : IConsumer<WithdrawalTriggered>
{
    public Task Consume(ConsumeContext<WithdrawalTriggered> context)
    {
        store.Append(AuditEntryFactory.From(context.Message, AuditConsumerHelper.MessageId(context), clock.UtcNow));
        return Task.CompletedTask;
    }
}

// FR-20, FR-15, #164, IADR-0089: バックテスト verdict（Stage 0 合格判定 #16・Stage 0→1 解錠）を中央監査台帳へ記録する。
internal sealed class BacktestEvaluatedAuditConsumer(IAuditEventStore store, IClock clock)
    : IConsumer<BacktestEvaluated>
{
    public Task Consume(ConsumeContext<BacktestEvaluated> context)
    {
        store.Append(AuditEntryFactory.From(context.Message, AuditConsumerHelper.MessageId(context), clock.UtcNow));
        return Task.CompletedTask;
    }
}

// FR-10, FR-11, UC-06, #292, IADR-0117: 利用者（owner）による建玉の手仕舞い要求を中央監査台帳へ記録する。
// 後続の OrderApproved はアクターも理由も持たないため、本記録が「誰が・なぜ落としたか」の唯一の証跡になる。
internal sealed class PositionCloseRequestedAuditConsumer(IAuditEventStore store, IClock clock)
    : IConsumer<PositionCloseRequested>
{
    public Task Consume(ConsumeContext<PositionCloseRequested> context)
    {
        store.Append(AuditEntryFactory.From(context.Message, AuditConsumerHelper.MessageId(context), clock.UtcNow));
        return Task.CompletedTask;
    }
}

// UC-01, FR-09, FR-07, #210: 日報未確定による取引スキップ（取引判断 #11）を中央監査台帳へ記録する（全イベントの時系列記録・FR-11）。
internal sealed class DailyPolicyUnconfirmedAuditConsumer(IAuditEventStore store, IClock clock)
    : IConsumer<DailyPolicyUnconfirmed>
{
    public Task Consume(ConsumeContext<DailyPolicyUnconfirmed> context)
    {
        store.Append(AuditEntryFactory.From(context.Message, AuditConsumerHelper.MessageId(context), clock.UtcNow));
        return Task.CompletedTask;
    }
}

// FR-05, FR-10, FR-11, #292, IADR-0118: ブローカ実ポジションの観測を中央監査台帳へ記録する（全イベントの時系列記録・FR-11）。
internal sealed class BrokerPositionsObservedAuditConsumer(IAuditEventStore store, IClock clock)
    : IConsumer<BrokerPositionsObserved>
{
    public Task Consume(ConsumeContext<BrokerPositionsObserved> context)
    {
        store.Append(AuditEntryFactory.From(context.Message, AuditConsumerHelper.MessageId(context), clock.UtcNow));
        return Task.CompletedTask;
    }
}

// FR-05, FR-10, FR-11, #292, IADR-0118: 台帳とブローカの乖離検知を中央監査台帳へ記録する。
// 是正は伴わないため、この記録が「いつ・どの銘柄で乖離が生じたか」の唯一の永続証跡になる。
internal sealed class PositionReconciliationDriftAuditConsumer(IAuditEventStore store, IClock clock)
    : IConsumer<PositionReconciliationDrift>
{
    public Task Consume(ConsumeContext<PositionReconciliationDrift> context)
    {
        store.Append(AuditEntryFactory.From(context.Message, AuditConsumerHelper.MessageId(context), clock.UtcNow));
        return Task.CompletedTask;
    }
}
