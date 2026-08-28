using System.Net;
using System.Text;
using System.Text.Json;
using NotificationService.Application.State;
using NotificationService.Infrastructure.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NotificationService.Infrastructure.Tests;

// FR-09, IADR-0020: Discord Webhook 実送信の POST 本文と非 2xx 例外化を fake HttpMessageHandler で検証する（実ネットワーク不使用）。
public class DiscordWebhookNotificationSenderTests
{
    private const string Url = "https://discord.com/api/webhooks/test";

    private static NotificationMessage Message() => new("取引実行", "約定 Filled 10@1050", NotificationSeverity.Info);

    [Fact]
    public async Task Webhook_へ_content_を_POST_する()
    {
        var handler = new CapturingHandler(HttpStatusCode.NoContent);
        var sender = new DiscordWebhookNotificationSender(
            new HttpClient(handler), Url, NullLogger<DiscordWebhookNotificationSender>.Instance);

        await sender.SendAsync(Message());

        handler.RequestUri.Should().Be(Url);
        handler.Method.Should().Be(HttpMethod.Post);
        // JSON は非 ASCII を \uXXXX にエスケープするため、content プロパティを復号して検証する。
        using var doc = JsonDocument.Parse(handler.Body);
        var content = doc.RootElement.GetProperty("content").GetString();
        content.Should().Contain("取引実行");
        content.Should().Contain("約定 Filled 10@1050");
    }

    [Fact]
    public async Task content_は_Discord_の_2000文字上限に切り詰められる()
    {
        var handler = new CapturingHandler(HttpStatusCode.NoContent);
        var sender = new DiscordWebhookNotificationSender(
            new HttpClient(handler), Url, NullLogger<DiscordWebhookNotificationSender>.Instance);

        // 上限を超える長文（タイトル＋本文で 2000 超）を送る。
        await sender.SendAsync(new NotificationMessage("長い通知", new string('x', 2500), NotificationSeverity.Warning));

        using var doc = JsonDocument.Parse(handler.Body);
        var content = doc.RootElement.GetProperty("content").GetString();
        content!.Length.Should().BeLessThanOrEqualTo(2000);
    }

    [Fact]
    public async Task 非_2xx_応答は例外化する()
    {
        var handler = new CapturingHandler(HttpStatusCode.TooManyRequests);
        var sender = new DiscordWebhookNotificationSender(
            new HttpClient(handler), Url, NullLogger<DiscordWebhookNotificationSender>.Instance);

        var act = () => sender.SendAsync(Message());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // 送信要求を捕捉し、指定ステータスを返す fake ハンドラ。
    private sealed class CapturingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            Method = request.Method;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(string.Empty, Encoding.UTF8) };
        }
    }
}
