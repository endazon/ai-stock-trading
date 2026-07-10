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
