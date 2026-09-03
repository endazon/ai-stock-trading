using System.Net;
using System.Text;
using System.Text.Json;
using NotificationService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NotificationService.Tests;

// FR-14, FR-07, UC-03〜05, ADR-0003, #341, IADR-0240: 報告書サービスのレビュー・確定エンドポイント呼び出しを
// fake HttpMessageHandler で検証する（実ネットワーク不使用）。kill switch と同じく
// **「失敗を成功に見せない」**ことが要である——確定したつもりで確定していない状態を作らない。
public class HttpReportReviewControllerTests
{
    private const string PeriodKey = "daily-2026-08-28";

    private static HttpReportReviewController Controller(FakeHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://report-service") },
            NullLogger<HttpReportReviewController>.Instance);

    [Fact]
    public async Task レビュー照会は_review_エンドポイントへ_GET_し版番号を返す()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"periodKey":"daily-2026-08-28","state":1,"version":3}""");

        var result = await Controller(handler).GetReviewAsync(PeriodKey);

        handler.RequestUri.Should().Be("http://report-service/reports/daily-2026-08-28/review");
        handler.Method.Should().Be(HttpMethod.Get);
        result.Succeeded.Should().BeTrue();
        result.Version.Should().Be(3);
    }

    [Fact]
    public async Task レビュー照会は_state_の表現に依存しない()
    {
        // IADR-0240 決定5: ReviewState（enum）は数値でも文字列でも来得るため読まない。版番号だけを射影する。
        var handler = new FakeHandler(
            HttpStatusCode.OK, """{"periodKey":"daily-2026-08-28","state":"PendingApproval","version":4}""");

        var result = await Controller(handler).GetReviewAsync(PeriodKey);

        result.Succeeded.Should().BeTrue();
        result.Version.Should().Be(4);
    }

    [Fact]
    public async Task 確定は_confirm_エンドポイントへ版番号つきで_POST_する()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"periodKey":"daily-2026-08-28","state":1}""");

        var result = await Controller(handler).ConfirmAsync(PeriodKey, 3);

        handler.RequestUri.Should().Be("http://report-service/reports/daily-2026-08-28/confirm");
        handler.Method.Should().Be(HttpMethod.Post);
        // 詳細設計07: 確定要求は 対象ID＋版番号 を必須とする。
        using var doc = JsonDocument.Parse(handler.Body);
        doc.RootElement.GetProperty("expectedVersion").GetInt32().Should().Be(3);

        result.Succeeded.Should().BeTrue();
        result.Confirmed.Should().BeTrue();
    }

    [Fact]
    public async Task 版不一致の_409_は受理されなかったこととして返る()
    {
        // サーバは正しく応答している（呼び出しの失敗ではない）。受理されなかったことを Confirmed=false で表す。
        var handler = new FakeHandler(HttpStatusCode.Conflict, """{"error":"版番号が一致しません。"}""");

        var result = await Controller(handler).ConfirmAsync(PeriodKey, 1);

        result.Succeeded.Should().BeTrue();
        result.Confirmed.Should().BeFalse();
        result.Message.Should().Contain("最新のドラフト");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task 確定の失敗を成功に見せない(HttpStatusCode status)
    {
        var handler = new FakeHandler(status, "{}");

        var result = await Controller(handler).ConfirmAsync(PeriodKey, 1);

        result.Succeeded.Should().BeFalse();
        result.Confirmed.Should().BeFalse();
    }

    [Fact]
    public async Task 認可の失敗には_owner_クライアント設定の手掛かりを添える()
    {
        var handler = new FakeHandler(HttpStatusCode.Forbidden, "{}");

        var result = await Controller(handler).ConfirmAsync(PeriodKey, 1);

        result.Message.Should().Contain("trading-owner");
    }

    [Fact]
    public async Task 解釈できない応答は版番号を騙らない()
    {
        // 2xx でも本文を解釈できなければ失敗として返す（誤った版で確定させない）。
        var handler = new FakeHandler(HttpStatusCode.OK, "null");

        var result = await Controller(handler).GetReviewAsync(PeriodKey);

        result.Succeeded.Should().BeFalse();
        result.Version.Should().Be(0);
    }

    [Fact]
    public async Task 例外は失敗として返り伝播しない()
    {
        var handler = new FakeHandler(new HttpRequestException("接続できません"));

        var result = await Controller(handler).ConfirmAsync(PeriodKey, 1);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("HttpRequestException");
    }

    [Fact]
    public async Task レビュー照会の失敗を成功に見せない()
    {
        var handler = new FakeHandler(HttpStatusCode.NotFound, "{}");

        var result = await Controller(handler).GetReviewAsync(PeriodKey);

        result.Succeeded.Should().BeFalse();
        result.Version.Should().Be(0, "版番号を騙らない（誤った版で確定させない）");
    }

    [Fact]
    public async Task 差し戻しの失敗を成功に見せない()
    {
        var handler = new FakeHandler(HttpStatusCode.Conflict, """{"error":"版番号が一致しません。"}""");

        var result = await Controller(handler).RequestChangesAsync(PeriodKey, 1);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task 差し戻しの解釈できない応答も失敗として返る()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "null");

        var result = await Controller(handler).RequestChangesAsync(PeriodKey, 1);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("解釈できません");
    }

    [Fact]
    public async Task タイムアウトは結果不明の失敗として返る()
    {
        // HttpClient のタイムアウトは OperationCanceledException で来る。**呼び出し側のキャンセルと区別**し、
        // 「結果は不明」と伝える（確定できたかどうか分からない状態を、成功にも明確な失敗にも寄せない）。
        var handler = new FakeHandler(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        var result = await Controller(handler).ConfirmAsync(PeriodKey, 1);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("結果は不明");
    }

    [Fact]
    public async Task 呼び出し側のキャンセルは伝播する()
    {
        // 否定形: 停止要求まで「失敗」に丸めない（ホスト停止時に無用な警告を出さない）。
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new FakeHandler(new OperationCanceledException(cts.Token));

        var act = async () => await Controller(handler).GetReviewAsync(PeriodKey, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task 差し戻しは_request_changes_エンドポイントへ版番号つきで_POST_する()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"periodKey":"daily-2026-08-28","state":2,"version":3}""");

        var result = await Controller(handler).RequestChangesAsync(PeriodKey, 3);

        handler.RequestUri.Should().Be("http://report-service/reports/daily-2026-08-28/request-changes");
        using var doc = JsonDocument.Parse(handler.Body);
        doc.RootElement.GetProperty("expectedVersion").GetInt32().Should().Be(3);
        result.Succeeded.Should().BeTrue();
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
