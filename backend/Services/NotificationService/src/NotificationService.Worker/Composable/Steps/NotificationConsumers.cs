using AiStockTrading.Notification.Application.Ports;
using AiStockTrading.Notification.Application.Services;
using AiStockTrading.Shared.Contracts.Events;
using MassTransit;

namespace AiStockTrading.Notification.Worker.Composable.Steps;

// FR-09, UC-01, UC-02, UC-06: 取引実行・リスク統制発動のイベントを購読し、テンプレート整形して通知する Consumer 群。
// 送信失敗（実 Discord 送信時）は例外化され MassTransit の再試行→デッドレターで回復性を担保する（IADR-0020）。

internal sealed class OrderExecutedNotificationConsumer(INotificationSender sender) : IConsumer<OrderExecuted>
{
    public Task Consume(ConsumeContext<OrderExecuted> context) =>
        sender.SendAsync(NotificationFormatter.From(context.Message), context.CancellationToken);
}

internal sealed class OrderRejectedNotificationConsumer(INotificationSender sender) : IConsumer<OrderRejected>
{
    public Task Consume(ConsumeContext<OrderRejected> context) =>
        sender.SendAsync(NotificationFormatter.From(context.Message), context.CancellationToken);
}

internal sealed class StopLossTriggeredNotificationConsumer(INotificationSender sender) : IConsumer<StopLossTriggered>
{
    public Task Consume(ConsumeContext<StopLossTriggered> context) =>
        sender.SendAsync(NotificationFormatter.From(context.Message), context.CancellationToken);
}

// FR-17, UC-06: 全体前提条件の変更（設定管理サービス #19）を購読して通知する。
internal sealed class AssumptionsChangedNotificationConsumer(INotificationSender sender) : IConsumer<AssumptionsChanged>
{
    public Task Consume(ConsumeContext<AssumptionsChanged> context) =>
        sender.SendAsync(NotificationFormatter.From(context.Message), context.CancellationToken);
}

// FR-07, FR-09: 報告書の確定（報告書サービス #14）を購読して通知する。
internal sealed class ReportConfirmedNotificationConsumer(INotificationSender sender) : IConsumer<ReportConfirmed>
{
    public Task Consume(ConsumeContext<ReportConfirmed> context) =>
        sender.SendAsync(NotificationFormatter.From(context.Message), context.CancellationToken);
}

// NFR（費用）, FR-09: 費用しきい値到達（費用統制 #23）を購読して通知する。
internal sealed class CostThresholdReachedNotificationConsumer(INotificationSender sender) : IConsumer<CostThresholdReached>
{
    public Task Consume(ConsumeContext<CostThresholdReached> context) =>
        sender.SendAsync(NotificationFormatter.From(context.Message), context.CancellationToken);
}

// FR-20, FR-09, UC-06: 撤退基準到達（撤退の定期評価ドライバ #166）を購読して利用者へ通知する。
internal sealed class WithdrawalTriggeredNotificationConsumer(INotificationSender sender) : IConsumer<WithdrawalTriggered>
{
    public Task Consume(ConsumeContext<WithdrawalTriggered> context) =>
        sender.SendAsync(NotificationFormatter.From(context.Message), context.CancellationToken);
}

// UC-01, FR-09, FR-07, #210: 日報未確定による取引スキップ（取引判断 #11）を購読して確定を促す通知を送る。
internal sealed class DailyPolicyUnconfirmedNotificationConsumer(INotificationSender sender) : IConsumer<DailyPolicyUnconfirmed>
{
    public Task Consume(ConsumeContext<DailyPolicyUnconfirmed> context) =>
        sender.SendAsync(NotificationFormatter.From(context.Message), context.CancellationToken);
}
