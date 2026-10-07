extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace TradeDecisionService.Tests;

// T-10-2311, FR-10, #1176, IADR-0495 決定1: Sizing:MinEntryNotionalRatio の読み取り。未設定は既定（1%）、範囲内は採用、
// 🔴 読めない値・範囲外は例外（Program.cs は構築時に読むので起動が止まる＝fail-fast）。
public class MinimumEntryNotionalOptionsLoaderTests
{
    private static MinimumEntryNotionalOptions Load(string? value)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(value is null ? [] : [new KeyValuePair<string, string?>(MinimumEntryNotionalOptionsLoader.Key, value)])
            .Build();
        return MinimumEntryNotionalOptionsLoader.FromConfiguration(config);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void T_10_2311_未設定なら既定の1パーセント(string? value)
    {
        Load(value).Ratio.Should().Be(TradingDefaults.MinEntryNotionalRatio);
        Load(value).Ratio.Should().Be(0.01m);
        MinimumEntryNotionalOptions.Default.Ratio.Should().Be(0.01m);
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("0.005", "0.005")]
    [InlineData("0.01", "0.01")]
    [InlineData("0.25", "0.25")]
    public void T_10_2311_範囲内の値を採用する(string value, string expected) =>
        Load(value).Ratio.Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("-0.01")]
    [InlineData("0.2501")]
    [InlineData("1")]
    [InlineData("abc")]
    [InlineData("1%")]
    public void T_10_2311_読めない値と範囲外は起動を止める(string value)
    {
        var act = () => Load(value);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{MinimumEntryNotionalOptionsLoader.Key}*起動を止めます*");
    }

    [Fact]
    public void T_10_2311_構成の型も範囲外を受け付けない()
    {
        ((Action)(() => _ = new MinimumEntryNotionalOptions(-0.001m))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => _ = new MinimumEntryNotionalOptions(0.26m))).Should().Throw<ArgumentOutOfRangeException>();
    }
}
