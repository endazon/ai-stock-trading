using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.InformationCollection.Application.State;
using AiStockTrading.InformationCollection.Domain;
using AiStockTrading.InformationCollection.Infrastructure.Composable.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.InformationCollection.Infrastructure.Tests;

// FR-01, ADR-0020, IADR-0022/0064: 情報源の選択。安全既定（外部接続しない）・設定不備での除外（IADR-0022）と、
// 案A+ の複数ソース合成・ソース単位の除外（IADR-0064）、および**名前つきで返す**ことを検証する。
public class InformationSourceFactoryTests
{
    private static readonly HttpClient Http = new();

    [Fact]
    public void 既定_provider未設定_は有効な情報源が0件()
    {
        Create(new CollectionSourceOptions()).Should().BeEmpty();
    }

    [Fact]
    public void none_指定_は有効な情報源が0件()
    {
        Create(new CollectionSourceOptions { Provider = "none" }).Should().BeEmpty();
    }

    [Fact]
    public void finnhub_かつ_APIキーと銘柄あり_は_FinnhubInformationSource()
    {
        var source = Create(Finnhub()).Should().ContainSingle().Which;

        source.Name.Should().Be("finnhub");
        source.Source.Should().BeOfType<FinnhubInformationSource>();
    }

    [Fact]
    public void finnhub_だが_APIキー未設定_は除外される()
    {
        var options = Finnhub();
        options.Finnhub.ApiKey = null;

        Create(options).Should().BeEmpty();
    }

    [Fact]
    public void finnhub_だが_銘柄未設定_は除外される()
    {
        var options = Finnhub();
        options.Finnhub.Symbols = [];

        Create(options).Should().BeEmpty();
    }

    [Fact]
    public void 未知の_provider_は安全側_収集しない()
    {
        Create(new CollectionSourceOptions { Provider = "bloomberg" }).Should().BeEmpty();
    }

    // ADR-0020 決定2: ニュース系 2 系統。**カタログの見出しと同じ名前で返る**こと（名前が違うと欠測判定に届かない）。
    [Fact]
    public void ニュース系2系統は構成が揃えばカタログと同じ名前で返る()
    {
        var options = Finnhub();
        options.Provider = "finnhub-news,google-news";
        options.GoogleNews = new GoogleNewsOptions { Queries = ["AAPL 株価"] };

        var sources = Create(options);

        sources.Select(s => s.Name).Should().Equal("finnhub-news", "google-news");
        sources[0].Source.Should().BeOfType<FinnhubCompanyNewsSource>();
        sources[1].Source.Should().BeOfType<GoogleNewsRssSource>();
        sources.Should().OnlyContain(s => InformationSourceCatalog.Default.Find(s.Name) != null);
    }

    [Fact]
    public void google_news_はクエリ未設定なら除外される()
    {
        var options = new CollectionSourceOptions { Provider = "google-news" };

        Create(options).Should().BeEmpty();
    }

    [Fact]
    public void 複数指定_は名前つきで並べて返す()
    {
        var options = Finnhub();
        options.Provider = "finnhub,sec-edgar,edinet,boj,fred";
        options.SecEdgar = new SecEdgarOptions
        {
            UserAgent = "AiStockTrading/1.0 (owner@example.com)",
            Ciks = ["320193"],
        };
        options.Edinet = new EdinetOptions { SubscriptionKey = "key" };
        options.Boj = new BojOptions { Db = "CO", SeriesCodes = ["CODE"] };
        options.Fred = new FredOptions { ApiKey = "key", SeriesIds = ["DGS10"] };

        Create(options).Select(s => s.Name).Should().Equal("finnhub", "sec-edgar", "edinet", "boj", "fred");
    }

    [Fact]
    public void 構成を欠くソースだけを除外し他のソースは有効なままにする()
    {
        // EDINET のキー切れで FRED まで止まると案A+ の冗長化の狙いに反するため、欠けたソースのみ落とす。
        var options = new CollectionSourceOptions
        {
            Provider = "edinet,fred",
            Fred = new FredOptions { ApiKey = "key", SeriesIds = ["DGS10"] },
        };

        Create(options).Select(s => s.Name).Should().Equal("fred");
    }

    // ADR-0005 決定5 / ADR-0020 決定5: 有料化の判断が下りるまでは**推奨へ一時降格**して運用を継続する。
    [Fact]
    public void 一時降格すると区分が推奨になり欠測時の扱いが記録のみになる()
    {
        var catalog = InformationSourceFactory.ApplyDemotions(
            InformationSourceCatalog.Default, "finnhub-news", NullLogger.Instance);

        var demoted = catalog.Find("finnhub-news")!;
        demoted.Tier.Should().Be(SourceTier.Recommended);
        demoted.MissingBehavior.Should().Be(MissingSourceBehavior.RecordAndNotifyOnly);

        // 降格していないソースは元のまま。
        catalog.Find("google-news")!.Tier.Should().Be(SourceTier.Required);
    }

    [Fact]
    public void 一時降格の指定が未知の名前ならカタログを変えない()
    {
        var catalog = InformationSourceFactory.ApplyDemotions(
            InformationSourceCatalog.Default, "bloomberg", NullLogger.Instance);

        catalog.Definitions.Should().HaveSameCount(InformationSourceCatalog.Default.Definitions);
        catalog.Find("finnhub-news")!.Tier.Should().Be(SourceTier.Required);
    }

    private static CollectionSourceOptions Finnhub() => new()
    {
        Provider = "finnhub",
        Finnhub = new FinnhubOptions { ApiKey = "key", Symbols = ["AAPL"] },
    };

    private static IReadOnlyList<NamedInformationSource> Create(CollectionSourceOptions options) =>
        InformationSourceFactory.Create(options, Http, new StubClock(), TimeProvider.System, NullLoggerFactory.Instance);

    private sealed class StubClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }
}
