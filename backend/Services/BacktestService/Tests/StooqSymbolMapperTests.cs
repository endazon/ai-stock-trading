using BacktestService.Features.Backtest;
using BacktestService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace BacktestService.Tests;

// FR-15, ADR-0004, #208, IADR-0105: 社内の銘柄表現（"AAPL" / "7203" ＋ Market）から Stooq の銘柄記法への写像。
public class StooqSymbolMapperTests
{
    [Theory]
    [InlineData("AAPL", Market.UnitedStates, "aapl.us")]
    [InlineData("brk.b", Market.UnitedStates, "brk.b.us")]
    [InlineData("7203", Market.Japan, "7203.jp")]
    [InlineData(" 7203 ", Market.Japan, "7203.jp")]
    public void 市場ごとの銘柄記法へ写像する(string symbol, Market market, string expected)
    {
        StooqSymbolMapper.TryMap(symbol, market, out var mapped).Should().BeTrue();
        mapped.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 空の銘柄は写像できない(string symbol)
    {
        StooqSymbolMapper.TryMap(symbol, Market.UnitedStates, out var mapped).Should().BeFalse();
        mapped.Should().BeEmpty();
    }

    [Fact]
    public void 未知の市場は写像できない()
    {
        // 将来 Market が増えたとき、黙って誤った記法で外部を叩かない（fail-safe）。
        StooqSymbolMapper.TryMap("AAPL", (Market)999, out var mapped).Should().BeFalse();
        mapped.Should().BeEmpty();
    }
}
