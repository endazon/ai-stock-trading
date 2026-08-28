using System.Net;
using InformationCollectionService.Application.Ports;
using InformationCollectionService.Domain;
using InformationCollectionService.Infrastructure.Adapters;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InformationCollectionService.Infrastructure.Tests;

// FR-01, ADR-0004, ADR-0020 決定2, IADR-0064: ニュース系 2 系統のコネクタ。
// **録画した応答**（実 API の形をそのまま写したフィクスチャ）を fake HttpMessageHandler で返し、
// 実ネットワークを使わずに写像・欠測の扱いを検証する（#336 受け入れ基準④）。
public class NewsInformationSourceTests
{
    // finnhub.io/api/v1/company-news の応答（必要な項目のみ）。
    private const string CompanyNewsJson =
        """
        [
          {
            "category": "company",
            "datetime": 1787000000,
            "headline": "Apple、第3四半期の決算を発表",
            "id": 7712345,
            "image": "https://example.invalid/image.png",
            "related": "AAPL",
            "source": "Example Wire",
            "summary": "売上高は市場予想を上回った。",
            "url": "https://example.invalid/news/1"
          },
          {
            "category": "company",
            "datetime": 1786990000,
            "headline": "",
            "id": 7712346,
            "related": "AAPL",
            "source": "Example Wire",
            "summary": "見出しの無い記事は採らない",
            "url": "https://example.invalid/news/2"
          }
        ]
        """;

    // news.google.com/rss/search の応答（RSS 2.0・必要な項目のみ）。
    private const string GoogleNewsRss =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0">
          <channel>
            <title>"AAPL" - Google ニュース</title>
            <item>
              <title>アップル株が上昇</title>
              <link>https://news.google.invalid/articles/1</link>
              <pubDate>Wed, 26 Aug 2026 09:00:00 GMT</pubDate>
              <source url="https://example.invalid">Example 新聞</source>
            </item>
            <item>
              <title>アップル、新製品を発表</title>
              <link>https://news.google.invalid/articles/2</link>
              <pubDate>Wed, 26 Aug 2026 08:00:00 GMT</pubDate>
              <source url="https://example.invalid">Example 経済</source>
            </item>
          </channel>
        </rss>
        """;

    // --- Finnhub 企業ニュース ---

    [Fact]
    public async Task 企業ニュースはニュース種別の_RawInformationItem_へ写像される()
    {
        var handler = new StubHandler(HttpStatusCode.OK, CompanyNewsJson);
        var source = FinnhubNews(handler, ["AAPL"]);

        var items = await source.FetchAsync();

        items.Should().ContainSingle("見出しの無い記事は採らない");
        var item = items[0];
        item.Kind.Should().Be(InformationKind.News);
        item.Source.Should().Be("finnhub-news");
        item.Symbol.Should().Be("AAPL", "銘柄との紐付けは提供側で済んでいる");
        item.Title.Should().Be("Apple、第3四半期の決算を発表");
        item.Content.Should().Contain("市場予想を上回った");
        item.Url.Should().Be("https://example.invalid/news/1");
        item.PublishedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1787000000));
    }

    [Fact]
    public async Task 企業ニュースは銘柄ごとにレート制限を待つ()
    {
        var limiter = new CountingRateLimiter();
        var source = FinnhubNews(new StubHandler(HttpStatusCode.OK, CompanyNewsJson), ["AAPL", "MSFT"], limiter);

        await source.FetchAsync();

        limiter.Waits.Should().Be(2);
    }

    // 🔴 **1 銘柄も取れなければ欠測として上へ返す**（握りつぶすとニュース系の全滅判定に届かない）。
    [Fact]
    public async Task 企業ニュースが全銘柄で失敗すると例外を投げる()
    {
        var source = FinnhubNews(new StubHandler(HttpStatusCode.TooManyRequests, "{}"), ["AAPL"]);

        var act = () => source.FetchAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // 一部の銘柄が失敗しても、取れている限りニュース系は生きている。
    [Fact]
    public async Task 企業ニュースは一部銘柄の失敗では例外を投げない()
    {
        var handler = new SequenceHandler(
            (HttpStatusCode.TooManyRequests, "{}"),
            (HttpStatusCode.OK, CompanyNewsJson));
        var source = FinnhubNews(handler, ["AAPL", "MSFT"]);

        var items = await source.FetchAsync();

        items.Should().ContainSingle();
    }

    [Fact]
    public async Task 企業ニュースの要求は取得期間と銘柄を含む()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]");
        var source = FinnhubNews(handler, ["AAPL"]);

        await source.FetchAsync();

        handler.LastUrl.Should().Contain("/company-news")
            .And.Contain("symbol=AAPL")
            .And.Contain("from=1969-12-31")
            .And.Contain("to=1970-01-01");
    }

    // --- Google News RSS ---

    [Fact]
    public async Task GoogleNews_RSS_はニュース種別へ写像される()
    {
        var handler = new StubHandler(HttpStatusCode.OK, GoogleNewsRss);
        var source = GoogleNews(handler, ["AAPL 株価"]);

        var items = await source.FetchAsync();

        items.Should().HaveCount(2);
        items[0].Kind.Should().Be(InformationKind.News);
        items[0].Source.Should().Be("google-news");
        items[0].Title.Should().Be("アップル株が上昇");
        items[0].Url.Should().Be("https://news.google.invalid/articles/1");
        items[0].PublishedAt.Should().Be(new DateTimeOffset(2026, 8, 26, 9, 0, 0, TimeSpan.Zero));
    }

    // 🔴 **クエリを銘柄として詐称しない。** 紐付けが提供側で済んでいるのは Finnhub 企業ニュースだけである。
    [Fact]
    public async Task GoogleNews_RSS_は銘柄を紐付けない()
    {
        var source = GoogleNews(new StubHandler(HttpStatusCode.OK, GoogleNewsRss), ["AAPL 株価"]);

        var items = await source.FetchAsync();

        items.Should().OnlyContain(i => i.Symbol == null);
    }

    [Fact]
    public async Task GoogleNews_RSS_は取得件数の上限を守る()
    {
        var source = GoogleNews(new StubHandler(HttpStatusCode.OK, GoogleNewsRss), ["AAPL 株価"], maxItems: 1);

        (await source.FetchAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task GoogleNews_RSS_が失敗すると例外を投げる()
    {
        var source = GoogleNews(new StubHandler(HttpStatusCode.ServiceUnavailable, ""), ["AAPL 株価"]);

        var act = () => source.FetchAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // 壊れた XML は「0 件」ではなく**欠測**として扱う（黙って空を返すと欠測が判定へ届かない）。
    [Fact]
    public async Task GoogleNews_RSS_の壊れた応答は欠測として扱う()
    {
        var source = GoogleNews(new StubHandler(HttpStatusCode.OK, "<rss><channel>"), ["AAPL 株価"]);

        var act = () => source.FetchAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static IInformationSource FinnhubNews(
        HttpMessageHandler handler, string[] symbols, IRateLimiter? limiter = null) =>
        new FinnhubCompanyNewsSource(
            new HttpClient(handler),
            "key",
            symbols,
            limiter ?? new CountingRateLimiter(),
            new StubClock(),
            NullLogger<FinnhubCompanyNewsSource>.Instance);

    private static IInformationSource GoogleNews(
        HttpMessageHandler handler, string[] queries, int maxItems = 20) =>
        new GoogleNewsRssSource(
            new HttpClient(handler),
            queries,
            new CountingRateLimiter(),
            NullLogger<GoogleNewsRssSource>.Instance,
            maxItems);

    private sealed class StubClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }
}
