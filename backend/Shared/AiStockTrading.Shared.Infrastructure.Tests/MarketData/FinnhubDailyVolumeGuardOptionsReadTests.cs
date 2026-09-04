using System.Collections.Generic;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.MarketData;

// FR-01, ADR-0031（計画）決定3, NFR, #679: 暫定日次上限を構成から読む際の型変換の規律。
//
// 🔴 **本テスト群は、実配備でしか出なかった欠陥の再発を止めるためにある。**
// chart の設定点は「キーは書くが値は空」で既定へ委ねる規約であり（`values.yaml` の多数のキーが
// `value: ""`）、`IConfiguration.Get<T>()` はその空文字を `int` へ変換できず例外を投げる。
// 実測（2026-09-03）では `Finnhub__ProvisionalDailyLimit: ""` を与えた RiskManagementService が
// 起動時に CrashLoopBackOff へ落ちた。**CI は配備しないため緑のまま通り抜けた。**
public class FinnhubDailyVolumeGuardOptionsReadTests
{
    private static IConfiguration ConfigWith(string? provisionalDailyLimit)
    {
        var values = new Dictionary<string, string?>();
        if (provisionalDailyLimit is not null)
        {
            values[$"{FinnhubDailyVolumeGuardOptions.SectionName}:{nameof(FinnhubDailyVolumeGuardOptions.ProvisionalDailyLimit)}"] =
                provisionalDailyLimit;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void 空文字は既定へ倒す_例外を投げない()
    {
        // 🔴 これが #679 の本体。chart が `value: ""` を渡しても落ちてはならない。
        var options = FinnhubDailyVolumeGuardOptions.Read(ConfigWith(string.Empty));

        options.ProvisionalDailyLimit.Should().Be(300);
    }

    [Fact]
    public void 未設定は既定へ倒す()
    {
        var options = FinnhubDailyVolumeGuardOptions.Read(ConfigWith(null));

        options.ProvisionalDailyLimit.Should().Be(300);
    }

    [Theory]
    [InlineData("450", 450)]
    [InlineData("1", 1)]
    public void 正の整数はそのまま採る(string raw, int expected)
    {
        var options = FinnhubDailyVolumeGuardOptions.Read(ConfigWith(raw));

        options.ProvisionalDailyLimit.Should().Be(expected);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12.5")]
    [InlineData("   ")]
    public void 不正値は既定へ倒す(string raw)
    {
        var options = FinnhubDailyVolumeGuardOptions.Read(ConfigWith(raw));

        options.ProvisionalDailyLimit.Should().Be(300);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void 非正値は既定へ倒す_上限0は常時超過になり統制の信号が雑音に埋もれるため(string raw)
    {
        var options = FinnhubDailyVolumeGuardOptions.Read(ConfigWith(raw));

        options.ProvisionalDailyLimit.Should().Be(300);
    }
}
