using System.Net;
using AiStockTrading.InformationCollection.Domain;
using AiStockTrading.InformationCollection.Worker.Composable.Adapters;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.InformationCollection.Worker.Tests;

// FR-01, ADR-0004: Finnhub 取得アダプタの応答写像・失敗スキップを fake HttpMessageHandler で検証する（実ネットワーク不使用）。
public class FinnhubInformationSourceTests
{
    [Fact]
    public async Task 応答を現在値の_RawInformationItem_に写像する()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"c":150.25,"h":151.0,"l":149.0,"o":149.5,"pc":148.0,"t":1720000000}""");
        var source = new FinnhubInformationSource(
            new HttpClient(handler), "key", ["AAPL"], new CountingRateLimiter(),
            NullLogger<FinnhubInformationSource>.Instance);

        var items = await source.FetchAsync();

        items.Should().ContainSingle();
        var item = items[0];
        item.Kind.Should().Be(InformationKind.Quote);
        item.Source.Should().Be("finnhub");
        item.Symbol.Should().Be("AAPL");
        item.Content.Should().Contain("current=150.25");
        handler.LastUrl.Should().Contain("symbol=AAPL");
        // API キーは URL に載せず（OTel への漏えい防止）、ヘッダーで渡す。
        handler.LastUrl.Should().NotContain("token");
        handler.LastToken.Should().Be("key");
    }

    [Fact]
    public async Task 取得失敗の銘柄はスキップされる()
    {
        var handler = new StubHandler(HttpStatusCode.TooManyRequests, "rate limited");
        var source = new FinnhubInformationSource(
            new HttpClient(handler), "key", ["AAPL"], new CountingRateLimiter(),
            NullLogger<FinnhubInformationSource>.Instance);

        var items = await source.FetchAsync();

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task 銘柄ごとにレート制限を待つ()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"c":150.25,"h":151.0,"l":149.0,"o":149.5,"pc":148.0,"t":1720000000}""");
        var limiter = new CountingRateLimiter();
        var source = new FinnhubInformationSource(
            new HttpClient(handler), "key", ["AAPL", "MSFT"], limiter,
            NullLogger<FinnhubInformationSource>.Instance);

        await source.FetchAsync();

        limiter.Waits.Should().Be(2, "Finnhub Free の 60回/分 制限を送信前に自制する（IADR-0065）");
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastUrl { get; private set; }

        public string? LastToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri?.ToString();
            LastToken = request.Headers.TryGetValues("X-Finnhub-Token", out var values) ? values.FirstOrDefault() : null;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
