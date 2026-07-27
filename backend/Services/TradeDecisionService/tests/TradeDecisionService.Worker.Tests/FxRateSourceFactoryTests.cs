using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TradeDecision.Worker.Composable.Adapters;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.TradeDecision.Worker.Tests;

// FR-10, #257, IADR-0106 決定5: FX レート源の選択。既定・構成不備はすべて no-op（実接続しない）へ倒す
// （MarketDataSourceFactory・IADR-0068 と同形）。
public class FxRateSourceFactoryTests
{
    [Fact]
    public void 既定_Provider未設定_は実接続しないno_op()
    {
        Create(new FxOptions()).Should().BeOfType<NoOpFxRateSource>();
    }

    [Theory]
    [InlineData("none")]
    [InlineData("")]
    [InlineData("   ")]
    public void noneや空指定は実接続しないno_op(string provider)
    {
        Create(new FxOptions { Provider = provider }).Should().BeOfType<NoOpFxRateSource>();
    }

    [Fact]
    public void 未知のproviderは実接続しないno_op()
    {
        // 起動は落とさない。レート無し＝非基準通貨の新規建て見送り（安全側）へ縮退する。
        Create(new FxOptions { Provider = "openexchangerates" }).Should().BeOfType<NoOpFxRateSource>();
    }

    [Fact]
    public void fred指定でもAPIキーが無ければ実接続しないno_op()
    {
        Create(new FxOptions { Provider = "fred" }).Should().BeOfType<NoOpFxRateSource>();
    }

    [Theory]
    [InlineData("fred")]
    [InlineData("Fred")]
    [InlineData(" FRED ")]
    public void fred指定かつAPIキーありで実レート源になる(string provider)
    {
        var source = Create(new FxOptions
        {
            Provider = provider,
            Fred = new FredFxOptions { ApiKey = "key" },
        });

        // 実レート源は TTL・鮮度上限の装飾を必ず経由する（生の取得器を直に配らない）。
        source.Should().BeOfType<CachingFxRateSource>();
    }

    [Fact]
    public void 選択中のproviderを自己申告する()
    {
        // 「有効化したつもりで効いていない」を introspection で検知できるよう、Create と同じ規則で解決する。
        FxRateSourceFactory.ResolveProvider(new FxOptions()).Should().Be("none");
        FxRateSourceFactory.ResolveProvider(new FxOptions { Provider = "fred" }).Should().Be("none");
        FxRateSourceFactory
            .ResolveProvider(new FxOptions { Provider = "fred", Fred = new FredFxOptions { ApiKey = "key" } })
            .Should().Be("fred");
    }

    [Fact]
    public async Task no_opは外貨のレートを解決しない()
    {
        var source = new NoOpFxRateSource(NullLogger<NoOpFxRateSource>.Instance);

        (await source.GetRateToBaseAsync(Currency.Usd)).Should().BeNull();
    }

    [Fact]
    public async Task no_opでも基準通貨はレート1で解決する()
    {
        // FX 未有効化の環境でも基準通貨（日本株）は従来どおり取引できる＝影響を非基準通貨に限定する（IADR-0106 決定3）。
        var source = new NoOpFxRateSource(NullLogger<NoOpFxRateSource>.Instance);

        var rate = await source.GetRateToBaseAsync(Currency.Jpy);

        rate!.Rate.Should().Be(1m);
    }

    private static IFxRateSource Create(FxOptions options) =>
        FxRateSourceFactory.Create(options, new HttpClient(), TimeProvider.System, NullLoggerFactory.Instance);
}
