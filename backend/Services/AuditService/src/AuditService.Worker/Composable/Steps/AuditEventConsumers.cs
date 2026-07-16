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
