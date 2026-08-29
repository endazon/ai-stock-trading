using NotificationService.Features.Notifications;
using Microsoft.Extensions.Logging;

namespace NotificationService.Infrastructure.ExternalServices;

// FR-14, IADR-0062 決定1: Bot の安全既定＝Gateway に接続しない no-op（IADR-0020 の LoggingNotificationSender と同型）。
// 未設定・設定不備のときに実 Discord へ接続してコマンドを受け付けることがないようにする。
public sealed class NullDiscordBotGateway(ILogger<NullDiscordBotGateway> logger) : IDiscordBotGateway
{
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Discord Bot は無効です（Gateway に接続しません・安全既定・IADR-0062）。");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
