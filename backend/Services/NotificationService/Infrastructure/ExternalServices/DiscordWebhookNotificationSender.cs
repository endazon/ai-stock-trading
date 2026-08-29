using System.Net.Http.Json;
using NotificationService.Features.Notifications;
using Microsoft.Extensions.Logging;

namespace NotificationService.Infrastructure.ExternalServices;

// FR-09, IADR-0020: Discord Webhook への実送信（縮退用 Webhook・詳細設計07 が許容）。Bot Gateway 送信 API は FR-14 後続。
// 非 2xx 応答は例外化し、メッセージングの再試行→デッドレターに委ねる（可用性 NFR）。
public sealed class DiscordWebhookNotificationSender(
    HttpClient httpClient,
    string webhookUrl,
    ILogger<DiscordWebhookNotificationSender> logger)
    : INotificationSender
{
    // Discord の 1 メッセージ content 上限（超過は恒常的に非 2xx となり通知が届かないため、上限内に切り詰める）。
    private const int DiscordContentLimit = 2000;

    public async Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Discord Webhook は { "content": "..." } を受け取る。タイトルは太字で先頭に付す。
        var content = string.IsNullOrEmpty(message.Title) ? message.Content : $"**{message.Title}**\n{message.Content}";

        // 上限超過（例: 拒否理由が多数）は恒久的失敗（デッドレター）になるため、末尾を切り詰めて送達を優先する。
        if (content.Length > DiscordContentLimit)
            content = content[..(DiscordContentLimit - 1)] + "…";

        using var response = await httpClient.PostAsJsonAsync(webhookUrl, new { content }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Discord Webhook 送信失敗: {Status}", (int)response.StatusCode);
            throw new InvalidOperationException($"Discord Webhook 送信に失敗しました（HTTP {(int)response.StatusCode}）。");
        }
    }
}
