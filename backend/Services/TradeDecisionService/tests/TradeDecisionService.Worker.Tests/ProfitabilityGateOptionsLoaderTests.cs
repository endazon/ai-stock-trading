using AiStockTrading.TradeDecision.Application.Services;
using AiStockTrading.TradeDecision.Worker.Composable.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AiStockTrading.TradeDecision.Worker.Tests;

// FR-17, IADR-0076: Profitability:* 構成の読み取りと安全側フォールバックの検証。
// 既定＝現行挙動（採算評価無効・判断費用 0）を config 経由で壊さないことを保証する。
public class ProfitabilityGateOptionsLoaderTests
{
    private static ProfitabilityGateOptions Load(params (string Key, string? Value)[] pairs)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();
        return ProfitabilityGateOptionsLoader.FromConfiguration(config);
    }

    [Fact]
    public void 未設定なら既定_無効_判断費用0()
    {
        var options = Load();

        options.Enabled.Should().BeFalse();
        options.DecisionCostJpy.Should().Be(0m);
    }

    [Fact]
    public void 全項目を設定から読み取る()
    {
        var options = Load(
            ("Profitability:Enabled", "true"),
            ("Profitability:DecisionCostJpy", "12.5"));

        options.Enabled.Should().BeTrue();
        options.DecisionCostJpy.Should().Be(12.5m);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("")]
    [InlineData("1")]
    public void 不正なEnabledは既定false_安全側(string raw)
    {
        Load(("Profitability:Enabled", raw)).Enabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("")]
    public void 不正または非正な判断費用は0のまま(string raw)
    {
        Load(("Profitability:DecisionCostJpy", raw)).DecisionCostJpy.Should().Be(0m);
    }
}
