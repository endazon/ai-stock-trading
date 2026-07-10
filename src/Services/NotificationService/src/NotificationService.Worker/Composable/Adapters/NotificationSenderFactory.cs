using AiStockTrading.Notification.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Notification.Worker.Composable.Adapters;

// FR-09, IADR-0020: 構成 Notifications:Provider による送信手段の選択。安全既定は no-op（外部送信しない）。
// discord-webhook 指定でも WebhookUrl 未設定なら no-op へフォールバックし警告する（設定不備で実送信を試みない）。
// 未知の provider も安全側（no-op）に倒す（通知は非critical のため host は落とさない）。IADR-0016 の実弾防止ゲートと同型。
internal static class NotificationSenderFactory
{
    public const string None = "none";
    public const string DiscordWebhook = "discord-webhook";

    public static INotificationSender Create(
        string? provider,
        string? webhookUrl,
        HttpClient httpClient,
        ILoggerFactory loggerFactory)
    {
        var selected = string.IsNullOrWhiteSpace(provider) ? None : provider.Trim().ToLowerInvariant();

        switch (selected)
        {
            case None:
                return NoOp(loggerFactory);

            case DiscordWebhook:
                if (string.IsNullOrWhiteSpace(webhookUrl))
                {
                    loggerFactory.CreateLogger(typeof(NotificationSenderFactory).FullName!).LogWarning(
                        "Notifications:Provider=discord-webhook ですが Notifications:Discord:WebhookUrl が未設定のため送信しません" +
                        "（no-op へフォールバック・IADR-0020）。");
                    return NoOp(loggerFactory);
                }

                return new DiscordWebhookNotificationSender(
                    httpClient, webhookUrl, loggerFactory.CreateLogger<DiscordWebhookNotificationSender>());

            default:
                loggerFactory.CreateLogger(typeof(NotificationSenderFactory).FullName!).LogWarning(
                    "未知の Notifications:Provider '{Provider}' のため送信しません（no-op・IADR-0020）。", provider);
                return NoOp(loggerFactory);
        }
    }

    private static LoggingNotificationSender NoOp(ILoggerFactory loggerFactory) =>
        new(loggerFactory.CreateLogger<LoggingNotificationSender>());
}
