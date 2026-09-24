using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationService.Infrastructure.ExternalServices;
using Xunit;

namespace NotificationService.Tests;

// FR-19, FR-10, UC-06, ADR-0028 決定1〜3, IADR-0182, #464:
// Risk の GFV 違反停止の解除エンドポイント呼び出しを fake HttpMessageHandler で検証する（実ネットワーク不使用）。
//
// NFR, #947, IADR-0397: 本番の組み立てはこのアダプタを IGoodFaithViolationController へ結線するが、既存の試験は
// 偽物（FakeController / StubGoodFaithViolationController）だけを使い、本物を 1 度も通していなかった
// （組み立てガード W3 の所見）。kill switch / pause と同じく「失敗を成功に見せない」ことと、
// 「解けたのは停止であって記録ではない」（決定1）を利用者へ伝えることを固定する。
public class HttpGoodFaithViolationControllerTests
{
    private static HttpGoodFaithViolationController Controller(FakeHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://risk-management-service") },
            NullLogger<HttpGoodFaithViolationController>.Instance);

    [Fact]
    public async Task 解除は_clear_エンドポイントへ理由つきで_POST_し_記録は失効しないと伝える()
    {
        var handler = new FakeHandler(
            HttpStatusCode.OK, """{"clearedOrderIds":["o-1","o-2"],"clearedAt":"2026-09-25T00:00:00Z","remainingCount":0}""");

        var result = await Controller(handler).ClearAsync("Discord Bot 経由の操作（actor=endazon）");

        handler.RequestUri.Should().Be("http://risk-management-service/risk-controls/good-faith-violations/clear");
        handler.Method.Should().Be(HttpMethod.Post);
        using var doc = JsonDocument.Parse(handler.Body);
        doc.RootElement.GetProperty("reason").GetString().Should().Contain("endazon");

        result.Succeeded.Should().BeTrue();
        result.Cleared.Should().BeTrue();
        result.Message.Should().Contain("2 件");
        result.Message.Should().Contain("失効しません", "解けたのは停止であって違反記録ではない（ADR-0028 決定1）");
        result.Message.Should().NotContain("残っており");
    }

    [Fact]
    public async Task 残件があれば停止が続くことを伝える()
    {
        var handler = new FakeHandler(
            HttpStatusCode.OK, """{"clearedOrderIds":["o-1"],"clearedAt":"2026-09-25T00:00:00Z","remainingCount":2}""");

        var result = await Controller(handler).ClearAsync("理由");

        result.Succeeded.Should().BeTrue();
        result.Cleared.Should().BeTrue();
        result.Message.Should().Contain("2 件が残っており停止は継続します");
    }

    // 422＝解除対象が無い・400＝理由欠如。どちらも「Risk は明確に応答した」（Succeeded）が、解除はしていない。
    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task 受理されない応答は解除していないと返し_理由を伝える(HttpStatusCode status)
    {
        var handler = new FakeHandler(status, """{"error":"解除対象の停止がありません"}""");

        var result = await Controller(handler).ClearAsync("理由");

        result.Succeeded.Should().BeTrue();
        result.Cleared.Should().BeFalse();
        result.Message.Should().Be("解除対象の停止がありません");
    }

    [Fact]
    public async Task 受理されない応答の本文が読めなくても解除していないと返す()
    {
        var handler = new FakeHandler(HttpStatusCode.UnprocessableEntity, "not json");

        var result = await Controller(handler).ClearAsync("理由");

        result.Cleared.Should().BeFalse();
        result.Message.Should().Contain("受理されませんでした");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task 認可失敗は失敗として返し_owner_クライアント設定を示唆する(HttpStatusCode status)
    {
        var handler = new FakeHandler(status, "");

        var result = await Controller(handler).ClearAsync("理由");

        result.Succeeded.Should().BeFalse();
        result.Cleared.Should().BeFalse();
        result.Message.Should().Contain("owner");
    }

    [Fact]
    public async Task 非2xx_は失敗として返す()
    {
        var handler = new FakeHandler(HttpStatusCode.InternalServerError, "");

        var result = await Controller(handler).ClearAsync("理由");

        result.Succeeded.Should().BeFalse();
        result.Cleared.Should().BeFalse();
        result.Message.Should().Contain("500");
    }

    [Fact]
    public async Task 応答本文を解釈できなければ失敗として返す()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "null");

        var result = await Controller(handler).ClearAsync("理由");

        result.Succeeded.Should().BeFalse();
        result.Cleared.Should().BeFalse();
    }

    [Fact]
    public async Task 例外は失敗として返し_送出しない()
    {
        var handler = new FakeHandler(new HttpRequestException("接続できません"));

        var result = await Controller(handler).ClearAsync("理由");

        result.Succeeded.Should().BeFalse();
        result.Cleared.Should().BeFalse();
        result.Message.Should().Contain(nameof(HttpRequestException));
    }

    [Fact]
    public async Task タイムアウトは状態不明として返す()
    {
        var handler = new FakeHandler(new TaskCanceledException("timeout"));

        var result = await Controller(handler).ClearAsync("理由");

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("状態は不明");
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
