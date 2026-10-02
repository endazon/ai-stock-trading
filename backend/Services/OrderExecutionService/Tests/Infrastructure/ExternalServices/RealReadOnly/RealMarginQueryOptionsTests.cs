using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using OrderExecutionService.Infrastructure.ExternalServices.RealReadOnly;
using Xunit;

namespace OrderExecutionService.Tests;

// T-10-2062, FR-10, #1000, IADR-0482 決定3: 実弾口座の読み取り専用の照会は**既定で無効**。明示した true だけが有効で、
// 未知の値は既定へ黙って倒さず起動時に止める。
public class RealMarginQueryOptionsTests
{
    private static RealMarginQueryOptions Parse(string? value) =>
        RealMarginQueryOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [RealMarginQueryOptions.EnabledKey] = value })
            .Build());

    [Fact]
    public void 未設定は無効()
    {
        RealMarginQueryOptions.FromConfiguration(new ConfigurationBuilder().Build()).Enabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("false")]
    [InlineData("False")]
    public void 空とfalseは無効(string value)
    {
        Parse(value).Enabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("true")]
    [InlineData(" TRUE ")]
    public void 明示したtrueだけが有効(string value)
    {
        Parse(value).Enabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("real")]
    [InlineData("on")] // 独立監査 🟢3（2026-10-02）: 慣用の真値でも有効にしない（明示の true だけ）。
    public void 未知の値は起動時に止める(string value)
    {
        var act = () => Parse(value);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{RealMarginQueryOptions.EnabledKey}*");
    }
}
