using System.Net;
using System.Text;
using ReportService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-16, #563, IADR-0268: 日報 §3 の建玉を権威源（GET /risk-controls/open-positions）から引く
// s2s 照会と、その fail-safe を fake HttpMessageHandler で検証する（実ネットワーク不使用）。
//
// 🔴 **供給不達は null（未供給）へ倒す。空列（建玉なし）と混ぜない**——
// 同居する `HttpPeriodFillSource`（不達＝空列）とは向きが逆である。**揃えてはならない。**
public class HttpOpenPositionSourceTests
{
    private static HttpOpenPositionSource Source(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://risk-management") },
            NullLogger<HttpOpenPositionSource>.Instance);

    [Fact]
    public async Task 建玉のエンドポイントを叩く()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]");

        await Source(handler).GetOpenPositionsAsync();

        handler.LastUri!.AbsolutePath.Should().Be("/risk-controls/open-positions");
    }

    // 🔴 **肯定形**: 権威源の建玉を報告書の行へ写す（列挙は数値で往復する）。
    [Fact]
    public async Task 権威源の建玉を報告書の行へ写す()
    {
        // market=1（UnitedStates）・side=1（Sell＝ショート）。
        var handler = new StubHandler(HttpStatusCode.OK, """
            [{"symbol":"TSLA","market":1,"side":1,"quantity":5,"entryPrice":240,"stopLossPrice":252}]
            """);

        var positions = await Source(handler).GetOpenPositionsAsync();

        var p = positions.Should().ContainSingle().Subject;
        p.Symbol.Should().Be("TSLA");
        p.Market.Should().Be(Market.UnitedStates);
        p.Side.Should().Be(TradeSide.Sell);
        p.Quantity.Should().Be(5);
        p.AverageEntryPrice.Should().Be(240m);
        p.StopLossPrice.Should().Be(252m);
    }

    // 本経路が運ばない項目は未供給のまま返す（後段が現在値だけを埋める）。
    [Fact]
    public async Task 本経路が運ばない項目は未供給のまま返す()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            [{"symbol":"TSLA","market":1,"side":1,"quantity":5,"entryPrice":240,"stopLossPrice":252}]
            """);

        var p = (await Source(handler).GetOpenPositionsAsync())!.Single();

        p.CurrentPrice.Should().BeNull();
        p.UnrealizedPnl.Should().BeNull();
        p.BorrowFeeTotal.Should().BeNull();
        p.HoldingDays.Should().BeNull();
    }

    // 🔴 **否定形（上の肯定形と対）**: 引けなかったことを「建玉なし」と書かない（空列へ倒さない）。
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task 非2xxは未供給へ倒す(HttpStatusCode status)
    {
        (await Source(new StubHandler(status, "[]")).GetOpenPositionsAsync()).Should().BeNull();
    }

    [Fact]
    public async Task 例外は未供給へ倒す()
    {
        (await Source(new ThrowingHandler()).GetOpenPositionsAsync()).Should().BeNull();
    }

    [Fact]
    public async Task 応答本文がnullなら未供給へ倒す()
    {
        (await Source(new StubHandler(HttpStatusCode.OK, "null")).GetOpenPositionsAsync()).Should().BeNull();
    }

    // 引けたが建玉が 1 件も無い＝**空列**（未供給ではない）。上の否定形と区別できることを固定する。
    [Fact]
    public async Task 建玉0件は空列であり未供給ではない()
    {
        var positions = await Source(new StubHandler(HttpStatusCode.OK, "[]")).GetOpenPositionsAsync();

        positions.Should().NotBeNull();
        positions!.Should().BeEmpty();
    }

    [Fact]
    public async Task 銘柄が空の行は落とす()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            [{"symbol":"","market":0,"side":0,"quantity":100,"entryPrice":2500,"stopLossPrice":2375},
             {"symbol":"7203","market":0,"side":0,"quantity":100,"entryPrice":2500,"stopLossPrice":2375}]
            """);

        var positions = await Source(handler).GetOpenPositionsAsync();

        positions.Should().ContainSingle().Which.Symbol.Should().Be("7203");
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
