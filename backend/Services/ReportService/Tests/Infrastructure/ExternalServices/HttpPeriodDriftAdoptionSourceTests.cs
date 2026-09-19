using System.Net;
using System.Text;
using ReportService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-11, ADR-0041 決定 1, #870, #859, IADR-0360 決定 2: 手動売買の取り込みの s2s 照会と、その倒し方を
// fake HttpMessageHandler で検証する（実ネットワーク不使用）。
//
// 🔴 **不達はすべて null（照会できていない）へ倒す。空列（該当なし）へ倒さない**
//（`HttpPeriodFillSource` とは向きが違う。空列は日報 §2-b で嘘になり、在庫の畳み込みからも落ちる）。
public class HttpPeriodDriftAdoptionSourceTests
{
    private static readonly DateOnly From = new(2026, 9, 14);
    private static readonly DateOnly To = new(2026, 9, 18);

    private static HttpPeriodDriftAdoptionSource Source(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://risk-management") },
            NullLogger<HttpPeriodDriftAdoptionSource>.Instance);

    [Fact]
    public async Task 期間を要求のクエリ文字列に載せる()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]");

        await Source(handler).GetDriftAdoptionsAsync(From, To);

        handler.LastUri!.AbsolutePath.Should().Be("/risk-controls/drift-adoptions");
        handler.LastUri.Query.Should().Contain("from=2026-09-14").And.Contain("to=2026-09-18");
    }

    [Fact]
    public async Task 台帳の取り込みを報告書の入力へ写す()
    {
        // 列挙は数値で往復する（権威源は JsonStringEnumConverter を構成していない）。market=1（UnitedStates）・side=1（Sell）。
        var handler = new StubHandler(HttpStatusCode.OK, """
            [{"adoptionId":"33333333-3333-3333-3333-333333333333","symbol":"TSLA","market":1,"side":1,
              "quantity":20,"ledgerQuantityBefore":20,"brokerQuantity":0,
              "observedAt":"2026-09-18T05:00:00+00:00","actor":"owner","reason":"アプリから直接売却",
              "adoptedAt":"2026-09-18T05:30:00+00:00","origin":1,"realizedPnlRecorded":false}]
            """);

        var adoptions = await Source(handler).GetDriftAdoptionsAsync(From, To);

        var a = adoptions.Should().ContainSingle().Subject;
        a.Symbol.Should().Be("TSLA");
        a.Market.Should().Be(Market.UnitedStates);
        a.Side.Should().Be(TradeSide.Sell);
        a.Quantity.Should().Be(20);
        a.LedgerQuantityBefore.Should().Be(20);
        a.BrokerQuantity.Should().Be(0);
        a.Actor.Should().Be("owner");
        a.Reason.Should().Be("アプリから直接売却");
        a.AdoptedAt.Should().Be(new DateTimeOffset(2026, 9, 18, 5, 30, 0, TimeSpan.Zero));
        a.ObservedAt.Should().Be(new DateTimeOffset(2026, 9, 18, 5, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task 該当が無い期間は空列を返す()
    {
        var adoptions = await Source(new StubHandler(HttpStatusCode.OK, "[]")).GetDriftAdoptionsAsync(From, To);

        // 🔴 空列は「該当なし」という**事実**であり、未供給ではない。
        adoptions.Should().NotBeNull().And.BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task 非_2xx_は未供給へ倒す(HttpStatusCode status)
    {
        var adoptions = await Source(new StubHandler(status, "")).GetDriftAdoptionsAsync(From, To);

        adoptions.Should().BeNull("🔴 空列（該当なし）へ倒すと §2-b が嘘をつき、在庫の畳み込みからも落ちる");
    }

    [Fact]
    public async Task 例外は未供給へ倒す()
    {
        var adoptions = await Source(new ThrowingHandler()).GetDriftAdoptionsAsync(From, To);

        adoptions.Should().BeNull();
    }

    [Fact]
    public async Task 不正な応答は未供給へ倒す()
    {
        var adoptions = await Source(new StubHandler(HttpStatusCode.OK, "null")).GetDriftAdoptionsAsync(From, To);

        adoptions.Should().BeNull();
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("接続できません");
    }
}
