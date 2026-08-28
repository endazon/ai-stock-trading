using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Kernel.Tests.Trading;

// FR-17, 05_trading-assumptions §1/§4/§6: 既定値（確定値＋未登録 0）を固定する。
public class TradingAssumptionsDefaultsTests
{
    [Fact]
    public void 譲渡益税率は_20_315パーセント()
    {
        TradingAssumptionsDefaults.Create().CapitalGainsTaxRate.Should().Be(0.20315m);
    }

    [Fact]
    public void 月次費用上限は総額2万_LLM1万5千_インフラ5千_データ0()
    {
        var limits = TradingAssumptionsDefaults.Create().CostLimits;
        limits.Total.Should().Be(20_000m);
        limits.Llm.Should().Be(15_000m);
        limits.Infrastructure.Should().Be(5_000m);
        limits.Data.Should().Be(0m);
    }

    [Fact]
    public void 手数料と為替スプレッドは未登録_ゼロ()
    {
        var a = TradingAssumptionsDefaults.Create();
        a.JapanCommission.Should().Be(new CommissionSchedule(0m, 0m, 0m));
        a.UnitedStatesCommission.Should().Be(new CommissionSchedule(0m, 0m, 0m));
        a.FxSpreadRatio.Should().Be(0m);
    }

    // FR-17, §4, #358, IADR-0173: 計画確定値は **2**（利用者決定 2026-07-23・「往復費用＋税の 2 倍」）。
    // 旧値 1.5 は計画確定前の暫定値（当時の計画は未確定の <1.5 倍>）であり、実装が追随していなかった。
    [Fact]
    public void 最小期待利益倍率は計画確定値の2()
    {
        TradingAssumptionsDefaults.Create().MinimumExpectedProfitMultiple.Should().Be(2m);
    }
}
