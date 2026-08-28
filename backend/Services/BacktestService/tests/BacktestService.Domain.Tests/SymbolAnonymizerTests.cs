using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace BacktestService.Domain.Tests;

// FR-15, ADR-0008: 銘柄匿名化（検証条件①）。LLM が銘柄を同定できないよう決定的に匿名化する。
public class SymbolAnonymizerTests
{
    [Fact]
    public void 同じ銘柄は常に同じ匿名IDになる_決定的()
    {
        var a = SymbolAnonymizer.Anonymize("AAPL", Market.UnitedStates);
        var b = SymbolAnonymizer.Anonymize("AAPL", Market.UnitedStates);
        a.Should().Be(b);
    }

    [Fact]
    public void 異なる銘柄は異なる匿名IDになる()
    {
        SymbolAnonymizer.Anonymize("AAPL", Market.UnitedStates)
            .Should().NotBe(SymbolAnonymizer.Anonymize("MSFT", Market.UnitedStates));
    }

    [Fact]
    public void 匿名IDは元の銘柄コードを含まない()
    {
        SymbolAnonymizer.Anonymize("AAPL", Market.UnitedStates)
            .Should().NotContain("AAPL");
    }

    [Fact]
    public void バーの匿名化は価格と日付と市場を保持し銘柄のみ置換する()
    {
        var bar = new PriceBar("AAPL", Market.UnitedStates, new DateOnly(2025, 1, 2), 10m, 12m, 9m, 11m, 500);
        var anon = SymbolAnonymizer.AnonymizeBar(bar);

        anon.Symbol.Should().Be(SymbolAnonymizer.Anonymize("AAPL", Market.UnitedStates));
        anon.Symbol.Should().NotBe("AAPL");
        anon.Market.Should().Be(Market.UnitedStates);
        anon.Open.Should().Be(10m);
        anon.Close.Should().Be(11m);
        anon.Date.Should().Be(new DateOnly(2025, 1, 2));
        anon.Volume.Should().Be(500);
    }
}
