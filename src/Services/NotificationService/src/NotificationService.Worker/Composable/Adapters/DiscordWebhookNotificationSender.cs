using System.Net.Http.Json;
using AiStockTrading.Notification.Application.Ports;
using AiStockTrading.Notification.Application.State;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Notification.Worker.Composable.Adapters;

// FR-09, IADR-0020: Discord Webhook への実送信（縮退用 Webhook・詳細設計07 が許容）。Bot Gateway 送信 API は FR-14 後続。
// 非 2xx 応答は例外化し、MassTransit の再試行→デッドレターに委ねる（可用性 NFR）。
internal sealed class DiscordWebhookNotificationSender(
    HttpClient httpClient,
    string webhookUrl,
    ILogger<DiscordWebhookNotificationSender> logger)
    : INotificationSender
{
    public async Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Discord Webhook は { "content": "..." } を受け取る。タイトルは太字で先頭に付す。
        var content = string.IsNullOrEmpty(message.Title) ? message.Content : $"**{message.Title}**\n{message.Content}";

        using var response = await httpClient.PostAsJsonAsync(webhookUrl, new { content }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Discord Webhook 送信失敗: {Status}", (int)response.StatusCode);
            throw new InvalidOperationException($"Discord Webhook 送信に失敗しました（HTTP {(int)response.StatusCode}）。");
        }
    }
}
