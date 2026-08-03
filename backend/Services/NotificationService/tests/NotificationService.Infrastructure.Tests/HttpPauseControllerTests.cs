using System.Net;
using System.Text;
using System.Text.Json;
using AiStockTrading.Notification.Infrastructure.Composable.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.Notification.Infrastructure.Tests;

// FR-10, FR-14, UC-06/07, ADR-0009, IADR-0075: Risk の pause/resume/status エンドポイント呼び出しを
// fake HttpMessageHandler で検証する（実ネットワーク不使用）。kill switch と同じく「失敗を成功に見せない」ことが要。
public class HttpPauseControllerTests
{
    private static HttpPauseController Controller(FakeHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://risk-management-service") },
            NullLogger<HttpPauseController>.Instance);

    [Fact]
    public async Task 一時停止は_pause_エンドポイントへ理由つきで_POST_する()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"paused":true}""");

        var result = await Controller(handler).PauseAsync("Discord Bot 経由の操作（actor=endazon）");

        handler.RequestUri.Should().Be("http://risk-management-service/risk-controls/pause");
        handler.Method.Should().Be(HttpMethod.Post);
        // ADR-0007: 理由必須。
        using var doc = JsonDocument.Parse(handler.Body);
        doc.RootElement.GetProperty("reason").GetString().Should().Contain("endazon");

        result.Succeeded.Should().BeTrue();
        result.Paused.Should().BeTrue();
    }

    [Fact]
    public async Task 再開は_resume_エンドポイントへ_POST_する()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"paused":false}""");

        var result = await Controller(handler).ResumeAsync("理由");

        handler.RequestUri.Should().Be("http://risk-management-service/risk-controls/resume");
        result.Succeeded.Should().BeTrue();
        result.Paused.Should().BeFalse();
    }

    [Fact]
    public async Task 状態照会は_status_エンドポイントへ_GET_し整形結果を返す()
    {
        // 真偽値・数値から表示テキストを組む（enum の JSON 表現に依存しない）。
        var body = """
        {
            "killSwitchEngaged": false,
            "dailyLossLockoutActive": false,
            "lockoutReleaseOn": null,
            "tradingPaused": true,
            "newEntriesBlocked": true,
            "stage": 1,
            "dailyRealizedPnl": -500,
            "unrealizedPnl": -1200,
            "dailyPnl": -1700,
            "dailyOrderedAmount": 40000,
            "maxDailyOrderAmount": 100000,
            "drawdownRatio": 0.05,
            "maxDrawdownRatio": 0.10,
            "openPositionCount": 3,
            "maxOpenPositions": 10
        }
        """;
        var handler = new FakeHandler(HttpStatusCode.OK, body);

        var result = await Controller(handler).GetStatusAsync();

        handler.RequestUri.Should().Be("http://risk-management-service/risk-controls/status");
        handler.Method.Should().Be(HttpMethod.Get);
        result.Succeeded.Should().BeTrue();
        // 成立中の統制（一時停止）と新規建て停止が表示に含まれる。
        result.Message.Should().Contain("一時停止");
        result.Message.Should().Contain("停止中");
        result.Message.Should().Contain("Stage 1");
    }

    // 401/403 は owner クライアント設定の不備（trading-service トークンでは OwnerOnly を通過できない）。
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task 認可失敗は失敗として返し_owner_クライアント設定を示唆する(HttpStatusCode status)
    {
        var handler = new FakeHandler(status, "");

        var result = await Controller(handler).PauseAsync("理由");

        result.Succeeded.Should().BeFalse();
        result.Paused.Should().BeFalse();
        result.Message.Should().Contain("owner");
    }

    [Fact]
    public async Task 非2xx_は失敗として返す()
    {
        var handler = new FakeHandler(HttpStatusCode.InternalServerError, "");

        var result = await Controller(handler).PauseAsync("理由");

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("500");
    }

    [Fact]
    public async Task 応答本文を解釈できなければ失敗として返す()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "null");

        var result = await Controller(handler).PauseAsync("理由");

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task 状態照会の非2xx_は失敗として返す()
    {
        var handler = new FakeHandler(HttpStatusCode.Forbidden, "");

        var result = await Controller(handler).GetStatusAsync();

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("owner");
    }

    [Fact]
    public async Task 例外は失敗として返し_送出しない()
    {
        var handler = new FakeHandler(new HttpRequestException("接続できません"));

        var result = await Controller(handler).PauseAsync("理由");

        result.Succeeded.Should().BeFalse();
        result.Paused.Should().BeFalse();
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
