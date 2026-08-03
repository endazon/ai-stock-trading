using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Configuration.Domain.Tests;

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

    [Fact]
    public void 最小期待利益倍率は_1_5()
    {
        TradingAssumptionsDefaults.Create().MinimumExpectedProfitMultiple.Should().Be(1.5m);
    }
}
