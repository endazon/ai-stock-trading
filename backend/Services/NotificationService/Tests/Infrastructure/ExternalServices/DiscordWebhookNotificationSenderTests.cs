using System.Net;
using System.Text;
using System.Text.Json;
using NotificationService.Features.Notifications;
using NotificationService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NotificationService.Tests;

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

    // FR-09, FR-14, IADR-0359, #867: 通知本文には外部由来の文字列（銘柄名・LLM の散文・利用者が入力した
    // 理由・確定者名）がそのまま載る。`allowed_mentions` を指定しないと Discord は本文を解釈して
    // `@everyone` / `@here` / ロールメンションを**実際に発火**させる。出口 1 箇所で締める。
    [Fact]
    public async Task Webhook_は_allowed_mentions_の_parse_を空で送る()
    {
        var handler = new CapturingHandler(HttpStatusCode.NoContent);
        var sender = new DiscordWebhookNotificationSender(
            new HttpClient(handler), Url, NullLogger<DiscordWebhookNotificationSender>.Instance);

        await sender.SendAsync(Message());

        // 実際に電線へ載る形を文字列で固定する（プロパティ名が camelCase 化されないことも併せて固定）。
        handler.Body.Should().Contain("\"allowed_mentions\":{\"parse\":[]}");

        using var doc = JsonDocument.Parse(handler.Body);
        var allowed = doc.RootElement.GetProperty("allowed_mentions");
        allowed.GetProperty("parse").GetArrayLength().Should().Be(0);
        // parse だけでなく、明示の許可リスト（users / roles）も持たせない（持つと個別 ID のメンションが通る）。
        allowed.TryGetProperty("users", out _).Should().BeFalse("明示の許可リストは持たせない");
        allowed.TryGetProperty("roles", out _).Should().BeFalse("明示の許可リストは持たせない");
    }

    // FR-09, FR-14, IADR-0359, #867: 本文のサニタイズ（`@` の置換）では代替しない——表示を壊さず、
    // **本文はそのまま**で、発火だけを止める層（`allowed_mentions`）が正しい。
    [Theory]
    [InlineData("@everyone")]
    [InlineData("@here")]
    [InlineData("<@&123456789012345678>")]
    [InlineData("<@987654321098765432>")]
    public async Task メンション文字列を含む本文でも_本文は無加工で_parse_は空のまま送られる(string injected)
    {
        var handler = new CapturingHandler(HttpStatusCode.NoContent);
        var sender = new DiscordWebhookNotificationSender(
            new HttpClient(handler), Url, NullLogger<DiscordWebhookNotificationSender>.Instance);

        await sender.SendAsync(new NotificationMessage(
            "報告書確定", $"日次報告書が確定しました（{injected} 経由）。", NotificationSeverity.Info));

        using var doc = JsonDocument.Parse(handler.Body);
        doc.RootElement.GetProperty("content").GetString().Should().Contain(injected);
        doc.RootElement.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength().Should().Be(0);
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
