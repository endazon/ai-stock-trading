using System.Net;
using System.Text;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationService.Infrastructure.ExternalServices;
using Xunit;

namespace NotificationService.Tests;

// 🔴 T-10-989, FR-10, FR-11, FR-14, UC-06, ADR-0041 決定 4, #871, IADR-0350, IADR-0423:
// リスク管理の乖離の取り込みエンドポイントの呼び出しを fake HttpMessageHandler で検証する（実ネットワーク不使用）。
// 受理（200）・受理不能（422）の本文の読み取りは送り手の本物の型による契約テスト（RiskControlOperationReadContractTests）が
// 固定する。ここでは**失敗を成功に見せない**経路（400・401/403・5xx・例外・タイムアウト・本文の解釈不能）と、
// 配備順の窓（応答に操作者が無い＝旧版のリスク管理）を黙らせないことを固定する。
public class HttpPositionDriftAdoptionControllerTests
{
    private static HttpPositionDriftAdoptionController Controller(FakeHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://risk-management-service") },
            NullLogger<HttpPositionDriftAdoptionController>.Instance);

    private static Task<NotificationService.Features.Notifications.PositionDriftAdoptionResult> AdoptAsync(FakeHandler handler) =>
        Controller(handler).AdoptAsync("AAPL", Market.UnitedStates, "証券会社のアプリで売却した", "endazon");

    // 400＝要求の不備（代理される利用者の値域外・操作者を特定できない・理由の欠如）。直し方が分かるよう本文を返すが、失敗として扱う。
    [Fact]
    public async Task 要求の不備は失敗として返し_理由の文言を伝える()
    {
        var handler = new FakeHandler(HttpStatusCode.BadRequest, """{"error":"代理される利用者（onBehalfOf）の形式が不正です。"}""");

        var result = await AdoptAsync(handler);

        result.Succeeded.Should().BeFalse();
        result.Adopted.Should().BeFalse();
        result.Message.Should().Contain("台帳は変わっていません").And.Contain("onBehalfOf");
    }

    [Fact]
    public async Task 受理されない応答の本文が読めなくても取り込んでいないと返す()
    {
        var handler = new FakeHandler(HttpStatusCode.UnprocessableEntity, "not json");

        var result = await AdoptAsync(handler);

        result.Adopted.Should().BeFalse();
        result.Message.Should().Contain("取り込みは行いませんでした");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task 認可失敗は失敗として返し_owner_クライアント設定を示唆する(HttpStatusCode status)
    {
        var result = await AdoptAsync(new FakeHandler(status, ""));

        result.Succeeded.Should().BeFalse();
        result.Adopted.Should().BeFalse();
        result.Message.Should().Contain("owner");
    }

    [Fact]
    public async Task 非2xx_は失敗として返す()
    {
        var result = await AdoptAsync(new FakeHandler(HttpStatusCode.InternalServerError, ""));

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("500");
    }

    [Fact]
    public async Task 応答本文を解釈できなければ失敗として返す()
    {
        var result = await AdoptAsync(new FakeHandler(HttpStatusCode.OK, "null"));

        result.Succeeded.Should().BeFalse();
        result.Adopted.Should().BeFalse();
    }

    [Fact]
    public async Task 例外は失敗として返し_送出しない()
    {
        var result = await AdoptAsync(new FakeHandler(new HttpRequestException("接続できません")));

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain(nameof(HttpRequestException));
    }

    [Fact]
    public async Task タイムアウトは台帳の状態が不明であると返す()
    {
        var result = await AdoptAsync(new FakeHandler(new TaskCanceledException("timeout")));

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("不明").And.Contain("二重には取り込まれません");
    }

    // 🔴 配備順の窓（作業仕様書 規則 11 の (b)）: 旧版のリスク管理は応答に操作者を返さない。取り込みは成功として伝えるが、
    // 操作者が記録されたかを確認できないことを**黙らせない**。
    [Fact]
    public async Task 応答に操作者が無ければ操作者を確認できないと添える()
    {
        var handler = new FakeHandler(
            HttpStatusCode.OK,
            """
            {"adoptionId":"5f2b1a64-9f6e-4b69-9d6f-0d6c5a0b7e11","symbol":"AAPL","market":1,"ledgerQuantityBefore":100,
             "ledgerQuantityAfter":0,"brokerQuantity":0,"observedAt":"2026-09-25T01:00:00+00:00","realizedPnlRecorded":false,
             "referencePrice":null,"estimatedPnlInBase":null,"adoptedAt":"2026-09-25T01:01:00+00:00"}
            """);

        var result = await AdoptAsync(handler);

        (result.Succeeded, result.Adopted).Should().Be((true, true));
        result.Message.Should().Contain("100 → 0").And.Contain(HttpPositionDriftAdoptionController.ActorUnconfirmed);
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

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_throw is not null)
                throw _throw;

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
