using AiStockTrading.InformationCollection.Application.Adapters;
using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.InformationCollection.Worker.Composable.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.InformationCollection.Worker.Tests;

// FR-01, IADR-0022/0064: 情報源の選択。安全既定（no-op）・設定不備フォールバック（IADR-0022）と、
// 案A+ の複数ソース合成・ソース単位の除外（IADR-0064）を検証する。
public class InformationSourceFactoryTests
{
    private static readonly HttpClient Http = new();

    [Fact]
    public void 既定_provider未設定_は_NoOpInformationSource()
    {
        Create(new CollectionSourceOptions()).Should().BeOfType<NoOpInformationSource>();
    }

    [Fact]
    public void none_指定_は_NoOpInformationSource()
    {
        Create(new CollectionSourceOptions { Provider = "none" }).Should().BeOfType<NoOpInformationSource>();
    }

    [Fact]
    public void finnhub_かつ_APIキーと銘柄あり_は_FinnhubInformationSource()
    {
        Create(Finnhub()).Should().BeOfType<FinnhubInformationSource>();
    }

    [Fact]
    public void finnhub_だが_APIキー未設定_は_no_opへフォールバックする()
    {
        var options = Finnhub();
        options.Finnhub.ApiKey = null;

        Create(options).Should().BeOfType<NoOpInformationSource>();
    }

    [Fact]
    public void finnhub_だが_銘柄未設定_は_no_opへフォールバックする()
    {
        var options = Finnhub();
        options.Finnhub.Symbols = [];

        Create(options).Should().BeOfType<NoOpInformationSource>();
    }

    [Fact]
    public void 未知の_provider_は安全側_no_opに倒す()
    {
        Create(new CollectionSourceOptions { Provider = "bloomberg" }).Should().BeOfType<NoOpInformationSource>();
    }

    [Fact]
    public void 複数指定_は_CompositeInformationSource_で束ねる()
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

        Create(options).Should().BeOfType<CompositeInformationSource>();
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

        Create(options).Should().BeOfType<FredInformationSource>("EDINET のみ除外され FRED は残る");
    }

    [Fact]
    public void 有効なソースが1件も無ければ_no_opに倒す()
    {
        var options = new CollectionSourceOptions { Provider = "edinet,fred" };

        Create(options).Should().BeOfType<NoOpInformationSource>();
    }

    [Fact]
    public void 空白や大文字混じりの指定も解釈する()
    {
        var options = Finnhub();
        options.Provider = " FINNHUB , none ";

        Create(options).Should().BeOfType<FinnhubInformationSource>();
    }

    [Theory]
    [InlineData(null, "320193")]
    [InlineData("AiStockTrading/1.0 (owner@example.com)", null)]
    public void sec_edgar_は_User_Agent_と_CIK_の両方が要る(string? userAgent, string? cik)
    {
        // SEC は連絡先入り User-Agent を規約で必須としているため、未設定のまま接続しない。
        var options = new CollectionSourceOptions
        {
            Provider = "sec-edgar",
            SecEdgar = new SecEdgarOptions { UserAgent = userAgent, Ciks = cik is null ? [] : [cik] },
        };

        Create(options).Should().BeOfType<NoOpInformationSource>();
    }

    [Fact]
    public void boj_は_DB_と系列コードが要る()
    {
        var options = new CollectionSourceOptions { Provider = "boj", Boj = new BojOptions { Db = "CO" } };

        Create(options).Should().BeOfType<NoOpInformationSource>();
    }

    [Fact]
    public void 一覧の空要素は未設定として扱う()
    {
        // 環境変数の空指定（Collection__Source__Fred__SeriesIds__0=""）で実体のない構成のまま有効化しない。
        var options = new CollectionSourceOptions
        {
            Provider = "fred",
            Fred = new FredOptions { ApiKey = "key", SeriesIds = ["", "  "] },
        };

        Create(options).Should().BeOfType<NoOpInformationSource>();
    }

    [Fact]
    public void fred_は_APIキーと系列IDが要る()
    {
        var options = new CollectionSourceOptions { Provider = "fred", Fred = new FredOptions { ApiKey = "key" } };

        Create(options).Should().BeOfType<NoOpInformationSource>();
    }

    private static CollectionSourceOptions Finnhub() => new()
    {
        Provider = "finnhub",
        Finnhub = new FinnhubOptions { ApiKey = "key", Symbols = ["AAPL"] },
    };

    private static IInformationSource Create(CollectionSourceOptions options) =>
        InformationSourceFactory.Create(options, Http, new StubClock(), TimeProvider.System, NullLoggerFactory.Instance);

    private sealed class StubClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }
}
