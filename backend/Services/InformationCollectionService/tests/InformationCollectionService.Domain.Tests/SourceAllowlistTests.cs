using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.InformationCollection.Domain.Tests;

// FR-01, ADR-0004: 案A+ の許可リスト（許可ソースのみ受理・大文字小文字無視・非許可は破棄）を検証する。
public class SourceAllowlistTests
{
    [Theory]
    [InlineData("finnhub")]
    [InlineData("Finnhub")]
    [InlineData("sec-edgar")]
    [InlineData("edinet")]
    public void 許可されたソースは受理される(string source)
    {
        SourceAllowlist.Default.IsAllowed(source).Should().BeTrue();
    }

    [Theory]
    [InlineData("random-blog")]
    [InlineData("")]
    [InlineData(null)]
    public void 非許可ソースは拒否される(string? source)
    {
        SourceAllowlist.Default.IsAllowed(source).Should().BeFalse();
    }
}
