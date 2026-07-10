using AiStockTrading.InformationCollection.Application.Adapters;
using AiStockTrading.InformationCollection.Worker.Composable.Adapters;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.InformationCollection.Worker.Tests;

// FR-01, IADR-0022: 情報源の選択と安全既定（no-op）・設定不備フォールバックを検証する。
public class InformationSourceFactoryTests
{
    private static readonly HttpClient Http = new();
    private static readonly string[] Symbols = ["AAPL"];

    [Fact]
    public void 既定_provider未設定_は_NoOpInformationSource()
    {
        InformationSourceFactory.Create(null, null, Symbols, Http, NullLoggerFactory.Instance)
            .Should().BeOfType<NoOpInformationSource>();
    }

    [Fact]
    public void finnhub_かつ_APIキーと銘柄あり_は_FinnhubInformationSource()
    {
        InformationSourceFactory.Create("finnhub", "key", Symbols, Http, NullLoggerFactory.Instance)
            .Should().BeOfType<FinnhubInformationSource>();
    }

    [Fact]
    public void finnhub_だが_APIキー未設定_は_no_opへフォールバックする()
    {
        InformationSourceFactory.Create("finnhub", null, Symbols, Http, NullLoggerFactory.Instance)
            .Should().BeOfType<NoOpInformationSource>();
    }

    [Fact]
    public void finnhub_だが_銘柄未設定_は_no_opへフォールバックする()
    {
        InformationSourceFactory.Create("finnhub", "key", [], Http, NullLoggerFactory.Instance)
            .Should().BeOfType<NoOpInformationSource>();
    }

    [Fact]
    public void 未知の_provider_は安全側_no_opに倒す()
    {
        InformationSourceFactory.Create("bloomberg", "key", Symbols, Http, NullLoggerFactory.Instance)
            .Should().BeOfType<NoOpInformationSource>();
    }
}
