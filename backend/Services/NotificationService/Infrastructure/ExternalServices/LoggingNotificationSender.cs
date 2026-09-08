using AiStockTrading.Shared.Contracts.Logging;
using NotificationService.Features.Notifications;
using Microsoft.Extensions.Logging;

namespace NotificationService.Infrastructure.ExternalServices;

// FR-09, IADR-0020: 既定の安全 sender。外部送信せずログに出力するだけ（no-op）。CI/dev の安全既定で、
// 実 Discord への誤送信を構造的に防ぐ。実送信は構成で discord-webhook を明示有効化したときのみ。
public sealed class LoggingNotificationSender(ILogger<LoggingNotificationSender> logger) : INotificationSender
{
    public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        // NFR, IADR-0316, #708: 通知本文には LLM 生成の報告書要約が入る。改行・ESC を素通しすると
        // 行指向のログへ偽の行を注入できる（CWE-117）。**発生源で正規化してから渡す。**
        logger.LogInformation("[通知/{Severity}] {Title}: {Content}",
            message.Severity, LogSanitizer.Sanitize(message.Title), LogSanitizer.Sanitize(message.Content));
        return Task.CompletedTask;
    }
}
