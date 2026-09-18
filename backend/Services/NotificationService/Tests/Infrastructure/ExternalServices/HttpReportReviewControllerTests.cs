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

    // ---- 一覧（入力補完の候補・#834） ----

    [Fact]
    public async Task 一覧は_reports_へ_GET_し会話キーを新しい順に返す()
    {
        // FR-14, #834: 並びは対象期間の開始日の降順（同日は会話キーの降順）。
        var handler = new FakeHandler(HttpStatusCode.OK, """
            [
              {"periodKey":"daily-2026-09-15","kind":0,"periodStart":"2026-09-15","state":0,"body":"本文"},
              {"periodKey":"weekly-2026-W38","kind":1,"periodStart":"2026-09-14","state":1,"body":"本文"},
              {"periodKey":"daily-2026-09-18","kind":0,"periodStart":"2026-09-18","state":0,"body":"本文"}
            ]
            """);

        var keys = await Controller(handler).ListPeriodKeysAsync();

        handler.RequestUri.Should().Be("http://report-service/reports");
        handler.Method.Should().Be(HttpMethod.Get);
        keys.Should().Equal("daily-2026-09-18", "daily-2026-09-15", "weekly-2026-W38");
    }

    [Fact]
    public async Task 一覧は状態の表現にも本文の有無にも依存しない()
    {
        // IADR-0240 決定4/決定5: 本文・要約は取りに行かず、状態 enum も読まない（数値でも文字列でも来得る）。
        // 射影するのは会話キーと並び替えに使う開始日だけである。
        var handler = new FakeHandler(HttpStatusCode.OK, """
            [
              {"periodKey":"daily-2026-09-18","state":"Confirmed","periodStart":"2026-09-18"},
              {"periodKey":"daily-2026-09-17","state":0}
            ]
            """);

        var keys = await Controller(handler).ListPeriodKeysAsync();

        keys.Should().Equal("daily-2026-09-18", "daily-2026-09-17");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task 一覧の取得に失敗したら候補なしで素通しする(HttpStatusCode status)
    {
        // 否定形（fail-safe・#834）: 補完は入力の補助であって統制ではない。**例外を投げず空で返す**
        //（投げると Discord.Net の補完ハンドラを通じて Bot の動作に響く）。
        var handler = new FakeHandler(status, "{}");

        var keys = await Controller(handler).ListPeriodKeysAsync();

        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task 一覧の例外も候補なしに倒れる()
    {
        var handler = new FakeHandler(new HttpRequestException("接続できません"));

        var keys = await Controller(handler).ListPeriodKeysAsync();

        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task 一覧の解釈できない応答も候補なしに倒れる()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "null");

        var keys = await Controller(handler).ListPeriodKeysAsync();

        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task 一覧の開始日が欠けていても落ちない()
    {
        // 解釈できない/欠落した開始日は末尾へ倒す（一覧ごと落とさない）。同順位は会話キーの降順。
        var handler = new FakeHandler(HttpStatusCode.OK, """
            [
              {"periodKey":"daily-2026-09-17"},
              {"periodKey":"daily-2026-09-18","periodStart":"2026-09-18"},
              {"periodKey":"monthly-2026-09","periodStart":"不明"}
            ]
            """);

        var keys = await Controller(handler).ListPeriodKeysAsync();

        keys.Should().Equal("daily-2026-09-18", "monthly-2026-09", "daily-2026-09-17");
    }

    // ---- 見つからないときの案内（#834） ----

    [Fact]
    public async Task 見つからない会話キーには形式の例を添えて案内する()
    {
        // FR-14, #834: 日付だけを入れて 404 になり、応答が「HTTP 404」だけだったため利用者が回復できなかった
        //（実測・4 回連続）。**何が悪いのか・正しい形が何かを伝える。**
        var handler = new FakeHandler(HttpStatusCode.NotFound, "{}");

        var result = await Controller(handler).GetReviewAsync("2026-09-15");

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("見つかりません").And.Contain("daily-2026-09-18");
        result.Message.Should().NotContain("HTTP 404", "状態番号だけの文言にしない");
    }

    [Fact]
    public async Task 確定と差し戻しの_404_にも同じ案内を返す()
    {
        var confirm = await Controller(new FakeHandler(HttpStatusCode.NotFound, "{}")).ConfirmAsync("nope", 1);
        var changes = await Controller(new FakeHandler(HttpStatusCode.NotFound, "{}")).RequestChangesAsync("nope", 1);

        confirm.Message.Should().Contain("daily-2026-09-18");
        changes.Message.Should().Contain("daily-2026-09-18");
    }

    [Fact]
    public async Task _404_以外の失敗の文言は従来どおり()
    {
        // 否定形: 変えたのは 404 だけである（500 は操作名と状態番号を返す）。
        var handler = new FakeHandler(HttpStatusCode.InternalServerError, "{}");

        var result = await Controller(handler).GetReviewAsync(PeriodKey);

        result.Message.Should().Contain("レビュー局面の照会").And.Contain("HTTP 500");
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
