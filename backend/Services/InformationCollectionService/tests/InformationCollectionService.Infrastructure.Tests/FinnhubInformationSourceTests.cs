using System.Net;
using AiStockTrading.InformationCollection.Domain;
using AiStockTrading.InformationCollection.Infrastructure.Composable.Adapters;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.InformationCollection.Infrastructure.Tests;

// FR-01, ADR-0004: Finnhub 取得アダプタの応答写像・失敗スキップを fake HttpMessageHandler で検証する（実ネットワーク不使用）。
// IADR-0068: HTTP は共有の FinnhubQuoteClient へ抽出した。本テストは情報収集側の写像（high/low/prevClose を含む
// 収集内容）が抽出後も不変であることの回帰テスト（受け入れ基準 7）。
public class FinnhubInformationSourceTests
{
    [Fact]
    public async Task 応答を現在値の_RawInformationItem_に写像する()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"c":150.25,"h":151.0,"l":149.0,"o":149.5,"pc":148.0,"t":1720000000}""");
        var source = new FinnhubInformationSource(Client(handler, new CountingRateLimiter()), ["AAPL"]);

        var items = await source.FetchAsync();

        items.Should().ContainSingle();
        var item = items[0];
        item.Kind.Should().Be(InformationKind.Quote);
        item.Source.Should().Be("finnhub");
        item.Symbol.Should().Be("AAPL");
        // 収集内容は現在値だけでなく high/low/prevClose を保つ（抽出前と同一）。
        item.Content.Should().Contain("current=150.25");
        item.Content.Should().Contain("high=151");
        item.Content.Should().Contain("low=149");
        item.Content.Should().Contain("prevClose=148");
        handler.LastUrl.Should().Contain("symbol=AAPL");
        // API キーは URL に載せず（OTel への漏えい防止）、ヘッダーで渡す。
        handler.LastUrl.Should().NotContain("token");
        handler.LastToken.Should().Be("key");
    }

    [Fact]
    public async Task 取得失敗の銘柄はスキップされる()
    {
        var handler = new StubHandler(HttpStatusCode.TooManyRequests, "rate limited");
        var source = new FinnhubInformationSource(Client(handler, new CountingRateLimiter()), ["AAPL"]);

        var items = await source.FetchAsync();

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task 銘柄ごとにレート制限を待つ()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"c":150.25,"h":151.0,"l":149.0,"o":149.5,"pc":148.0,"t":1720000000}""");
        var limiter = new CountingRateLimiter();
        var source = new FinnhubInformationSource(Client(handler, limiter), ["AAPL", "MSFT"]);

        await source.FetchAsync();

        limiter.Waits.Should().Be(2, "Finnhub Free の 60回/分 制限を送信前に自制する（IADR-0064）");
    }

    private static FinnhubQuoteClient Client(HttpMessageHandler handler, IRateLimiter limiter) =>
        new(new HttpClient(handler), "key", limiter, NullLogger.Instance);

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
