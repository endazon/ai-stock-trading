using System.Net;
using System.Text;
using System.Text.Json;
using NotificationService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NotificationService.Tests;

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
        // FR-11・ADR-0009: 理由必須。
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

    // 🔴 T-10-943, FR-14, FR-10, #990, IADR-0408（2026-09-25 追記）: 上限の項目が応答に無い（上限を出さない版の送り手）ときも
    // 照会は成功し、上限を「不明」と表示する。0 へ倒さない（欠落を既定値 0 で読むと「上限 0」と読める）。
    [Fact]
    public async Task 状態照会は日次発注の上限が欠落していても成功し上限を不明と表示する()
    {
        var body = """
        {
            "killSwitchEngaged": false,
            "dailyLossLockoutActive": false,
            "lockoutReleaseOn": null,
            "tradingPaused": false,
            "newEntriesBlocked": false,
            "stage": 1,
            "dailyRealizedPnl": 0,
            "unrealizedPnl": 0,
            "dailyPnl": 0,
            "dailyOrderedAmount": 0,
            "drawdownRatio": 0,
            "maxDrawdownRatio": 0.10,
            "openPositionCount": 0,
            "maxOpenPositions": 10
        }
        """;

        var result = await Controller(new FakeHandler(HttpStatusCode.OK, body)).GetStatusAsync();

        result.Succeeded.Should().BeTrue(result.Message);
        result.Message.Should().Contain($"上限 {HttpPauseController.UnknownDailyOrderCap}")
            .And.NotContain($"/{0m:N0} 円");
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
