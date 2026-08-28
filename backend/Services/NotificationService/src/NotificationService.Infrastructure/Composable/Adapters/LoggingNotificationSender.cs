using NotificationService.Application.Ports;
using NotificationService.Application.State;
using Microsoft.Extensions.Logging;

namespace NotificationService.Infrastructure.Adapters;

// FR-09, IADR-0020: 既定の安全 sender。外部送信せずログに出力するだけ（no-op）。CI/dev の安全既定で、
// 実 Discord への誤送信を構造的に防ぐ。実送信は構成で discord-webhook を明示有効化したときのみ。
internal sealed class LoggingNotificationSender(ILogger<LoggingNotificationSender> logger) : INotificationSender
{
    public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        logger.LogInformation("[通知/{Severity}] {Title}: {Content}", message.Severity, message.Title, message.Content);
        return Task.CompletedTask;
    }
}
