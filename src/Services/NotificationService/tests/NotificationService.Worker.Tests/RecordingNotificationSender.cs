using System.Collections.Concurrent;
using AiStockTrading.Notification.Application.Ports;
using AiStockTrading.Notification.Application.State;

namespace AiStockTrading.Notification.Worker.Tests;

// テスト用の記録 sender。送信されたメッセージを収集する（外部送信しない）。
internal sealed class RecordingNotificationSender : INotificationSender
{
    public ConcurrentQueue<NotificationMessage> Sent { get; } = new();

    public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }
}
