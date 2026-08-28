using NotificationService.Infrastructure.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NotificationService.Infrastructure.Tests;

// FR-09, IADR-0020: 送信手段の選択と安全既定（no-op）・設定不備フォールバックを検証する。
public class NotificationSenderFactoryTests
{
    private static readonly HttpClient Http = new();

    [Fact]
    public void 既定_provider未設定_は_no_opの_LoggingNotificationSender()
    {
        var sender = NotificationSenderFactory.Create(null, null, Http, NullLoggerFactory.Instance);
        sender.Should().BeOfType<LoggingNotificationSender>();
    }

    [Fact]
    public void none_も_no_op()
    {
        NotificationSenderFactory.Create("none", "https://example.com/hook", Http, NullLoggerFactory.Instance)
            .Should().BeOfType<LoggingNotificationSender>();
    }

    [Fact]
    public void discord_webhook_かつ_URL設定_は実送信の_DiscordWebhookNotificationSender()
    {
        var sender = NotificationSenderFactory.Create(
            "discord-webhook", "https://discord.com/api/webhooks/xxx", Http, NullLoggerFactory.Instance);
        sender.Should().BeOfType<DiscordWebhookNotificationSender>();
    }

    [Fact]
    public void discord_webhook_だが_URL未設定_は_no_opへフォールバックする()
    {
        NotificationSenderFactory.Create("discord-webhook", null, Http, NullLoggerFactory.Instance)
            .Should().BeOfType<LoggingNotificationSender>();
    }

    [Fact]
    public void 未知の_provider_は安全側_no_opに倒す()
    {
        NotificationSenderFactory.Create("teams", null, Http, NullLoggerFactory.Instance)
            .Should().BeOfType<LoggingNotificationSender>();
    }
}
