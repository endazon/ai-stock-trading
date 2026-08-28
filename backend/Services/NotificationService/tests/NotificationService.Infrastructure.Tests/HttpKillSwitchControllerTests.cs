using System.Net;
using System.Text;
using System.Text.Json;
using NotificationService.Infrastructure.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NotificationService.Infrastructure.Tests;

// FR-14, UC-06, IADR-0062 決定4: Risk の kill switch エンドポイント呼び出しを fake HttpMessageHandler で検証する
// （実ネットワーク不使用）。受け入れ基準12。
// 「失敗を成功に見せない」ことが本アダプタの要（停止したつもりで停止していない状態を作らない）。
public class HttpKillSwitchControllerTests
{
    private static HttpKillSwitchController Controller(FakeHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://risk-management-service") },
            NullLogger<HttpKillSwitchController>.Instance);

    [Fact]
    public async Task 起動は_engage_エンドポイントへ理由つきで_POST_する()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"engaged":true}""");

        var result = await Controller(handler).EngageAsync("Discord Bot 経由の操作（actor=endazon）");

        handler.RequestUri.Should().Be("http://risk-management-service/risk-controls/kill-switch/engage");
        handler.Method.Should().Be(HttpMethod.Post);
        // FR-11・ADR-0003: 理由必須。
        using var doc = JsonDocument.Parse(handler.Body);
        doc.RootElement.GetProperty("reason").GetString().Should().Contain("endazon");

        result.Succeeded.Should().BeTrue();
        result.Engaged.Should().BeTrue();
    }

    [Fact]
    public async Task 解除は_disengage_エンドポイントへ_POST_する()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"engaged":false}""");

        var result = await Controller(handler).DisengageAsync("理由");

        handler.RequestUri.Should().Be("http://risk-management-service/risk-controls/kill-switch/disengage");
        result.Succeeded.Should().BeTrue();
        result.Engaged.Should().BeFalse();
    }

    // 401/403 は owner クライアント設定の不備（trading-service トークンでは OwnerOnly を通過できない）。
    // 切り分けできる文言を返し、成功に見せない。
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task 認可失敗は失敗として返し_owner_クライアント設定を示唆する(HttpStatusCode status)
    {
        var handler = new FakeHandler(status, "");

        var result = await Controller(handler).EngageAsync("理由");

        result.Succeeded.Should().BeFalse();
        result.Engaged.Should().BeFalse();
        result.Message.Should().Contain("owner");
    }

    [Fact]
    public async Task 非2xx_は失敗として返す()
    {
        var handler = new FakeHandler(HttpStatusCode.InternalServerError, "");

        var result = await Controller(handler).EngageAsync("理由");

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("500");
    }

    // 2xx でも本文を解釈できなければ状態を騙らない（停止したと誤認させない）。
    [Fact]
    public async Task 応答本文を解釈できなければ失敗として返す()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "null");

        var result = await Controller(handler).EngageAsync("理由");

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task 例外は失敗として返し_送出しない()
    {
        var handler = new FakeHandler(new HttpRequestException("接続できません"));

        var result = await Controller(handler).EngageAsync("理由");

        result.Succeeded.Should().BeFalse();
        result.Engaged.Should().BeFalse();
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly Exception? _throw;

        public FakeHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public FakeHandler(Exception toThrow)
        {
            _throw = toThrow;
            _body = string.Empty;
        }

        public string? RequestUri { get; private set; }

        public HttpMethod? Method { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            Method = request.Method;
            if (request.Content is not null)
                Body = await request.Content.ReadAsStringAsync(cancellationToken);

            if (_throw is not null)
                throw _throw;

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
