extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace TradeDecisionService.Tests;

// 🔴 FR-10, ADR-0066 決定1, #1291, IADR-0527 決定2（2026-10-13 追記）: 高ボラティリティ銘柄の区分の上限の値域の下端は、
// 最小の名目額の比率の**既定値（1%）の固定値**であり、取引判断の構成値 Sizing:MinEntryNotionalRatio に連動しない。
// 構成値を既定から変えたときのずれは、起動時の警告で知らせる（ADR-0066 決定1「構成値を変えるときは関係を確かめる」）。
public class HighVolatilityFloorConfigIndependenceTests
{
    private static MinimumEntryNotionalOptions Load(string value) =>
        MinimumEntryNotionalOptionsLoader.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>(MinimumEntryNotionalOptionsLoader.Key, value)])
            .Build());

    private static IReadOnlyList<string> Validate(decimal ratio) =>
        HighVolatilityOrderCap.Validate(TradingDefaults.CreateHighVolatilitySettings() with { MaxOrderAmountRatio = ratio });

    // T-10-2588, ADR-0066 決定1: 構成値を 0 / 0.5% / 1% / 2% / 6% にしても、区分の上限の値域の下端は 1% のまま
    // （1% は受理・0.99% は拒否）。構成値を上げた環境では「1% 以上・構成値未満」も受理する（厳しい側。拒否しない）。
    [Theory]
    [InlineData("0")]
    [InlineData("0.005")]
    [InlineData("0.01")]
    [InlineData("0.02")]
    [InlineData("0.06")]
    public void T_10_2588_区分の上限の下端は構成値に連動せず既定値の1パーセントに固定(string configured)
    {
        var options = Load(configured);

        HighVolatilityOrderCap.MinRatioLowerBound.Should().Be(0.01m, "ADR-0066 決定1: 下端は既定値 1% の固定値");
        HighVolatilityOrderCap.MinRatioLowerBound.Should().Be(TradingDefaults.MinEntryNotionalRatio);
        Validate(0.01m).Should().BeEmpty($"構成値 {options.Ratio} でも下端ちょうどは受理する");
        Validate(0.0099m).Should().ContainSingle($"構成値 {options.Ratio} でも下端未満は拒否する");
        if (options.Ratio > 0.01m)
            Validate(options.Ratio - 0.001m).Should().BeEmpty("構成値未満の区分の上限も拒否しない（必ず見送りになる値＝厳しい側）");
    }

    // T-10-2589, ADR-0066 決定1: 構成値が下端（1%）と一致すれば起動時の警告は出ない（既定の構成〔キー未設定〕を含む）。
    [Fact]
    public void T_10_2589_構成値が下端と一致すれば警告しない()
    {
        HighVolatilityFloorStartupNotice.WarningFor(MinimumEntryNotionalOptions.Default).Should().BeNull();
        HighVolatilityFloorStartupNotice.WarningFor(Load("0.01")).Should().BeNull();
        HighVolatilityFloorStartupNotice.WarningFor(Load("0.010")).Should().BeNull();
    }

    // T-10-2590, ADR-0066 決定1: 構成値が下端より上なら「区分の上限を構成値未満にすると必ず見送り」、下なら「区分の上限は 1% より
    // 下げられない」を警告する。どちらも起動は止めない（統制を緩めないため）。
    [Theory]
    [InlineData("0.02", "上回って", "必ず見送られる")]
    [InlineData("0.25", "上回って", "必ず見送られる")]
    [InlineData("0.005", "下回って", "下げられない")]
    [InlineData("0", "下回って", "下げられない")]
    public void T_10_2590_構成値が下端とずれれば向きに応じて警告する(string configured, string direction, string effect)
    {
        var warning = HighVolatilityFloorStartupNotice.WarningFor(Load(configured));

        warning.Should().NotBeNull();
        warning.Should().Contain("Sizing:MinEntryNotionalRatio").And.Contain("ADR-0066").And.Contain(direction).And.Contain(effect);
    }
}
