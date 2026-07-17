using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.MarketData;

// #158, FR-10, IADR-0068 決定 6: 現在値ソースの選択。既定・構成不備はすべて no-op（実接続しない）へ倒す。
public class MarketDataSourceFactoryTests
{
    [Fact]
    public void 既定_Provider未設定_は実接続しないno_op()
    {
        var source = Create(new MarketDataOptions());

        source.Should().BeOfType<NoOpMarketDataSource>();
    }

    [Theory]
    [InlineData("none")]
    [InlineData("")]
    [InlineData("   ")]
    public void noneや空指定は実接続しないno_op(string provider)
    {
        var source = Create(new MarketDataOptions { Provider = provider });

        source.Should().BeOfType<NoOpMarketDataSource>();
    }

    [Fact]
    public void 未知のproviderは実接続しないno_op()
    {
        // 起動を失敗させない（構成ミスで落とすより、含み 0 へ倒す方が安全側）。
        var source = Create(new MarketDataOptions { Provider = "bloomberg" });

        source.Should().BeOfType<NoOpMarketDataSource>();
    }

    [Fact]
    public void finnhub指定でもAPIキーが無ければ実接続しないno_op()
    {
        var source = Create(new MarketDataOptions { Provider = "finnhub" });

        source.Should().BeOfType<NoOpMarketDataSource>();
    }

    [Theory]
    [InlineData("finnhub")]
    [InlineData("Finnhub")]
    [InlineData(" FINNHUB ")]
    public void finnhub指定かつAPIキーありで実市況ソースになる(string provider)
    {
        var source = Create(new MarketDataOptions
        {
            Provider = provider,
            Finnhub = new FinnhubMarketDataOptions { ApiKey = "key" },
        });

        source.Should().BeOfType<FinnhubMarketDataSource>();
    }

    private static Shared.Contracts.Ports.IMarketDataSource Create(MarketDataOptions options) =>
        MarketDataSourceFactory.Create(options, new HttpClient(), TimeProvider.System, NullLoggerFactory.Instance);
}
