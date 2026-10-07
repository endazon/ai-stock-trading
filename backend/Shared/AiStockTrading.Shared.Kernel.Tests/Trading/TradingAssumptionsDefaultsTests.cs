using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Kernel.Tests.Trading;

// FR-17, 05_trading-assumptions §1/§2/§4/§6: 既定値（確定値＋§2 の暫定値＋未登録 0）を固定する。
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

    // FR-17, 05_trading-assumptions §2「米国株 売却時諸費用」, 計画 ADR-0035 決定 5, #1201, IADR-0501:
    // 既定値は**計画の暫定値**（確認日 2026-09-05）に一致する。改定時は計画と同時にこのテストを更新する。
    [Fact]
    public void 米国株の売却時諸費用は計画の暫定値_SEC_20_60ドル毎百万ドル_TAF_0_000166ドル毎株_上限8_30ドル()
    {
        var fees = TradingAssumptionsDefaults.Create().UnitedStatesSellRegulatoryFees;

        fees.Should().Be(new UsSellRegulatoryFeeSchedule(
            SecFeePerMillion: 20.60m, TafPerShare: 0.000166m, TafCapPerTrade: 8.30m));
        TradingAssumptionsDefaults.UnitedStatesSellRegulatoryFees.Should().Be(fees);
    }

    // 🔴 欄を運ばない受け手（gRPC／HTTP の受け取り・欄の無い永続化行）は**計画の暫定値**で埋まる（0 へ倒れない）。
    [Fact]
    public void 売却時諸費用を指定せずに組み立てた前提条件は計画の暫定値を持つ()
    {
        var a = new TradingAssumptions
        {
            CapitalGainsTaxRate = 0.20315m,
            JapanCommission = new CommissionSchedule(0m, 0m, 0m),
            UnitedStatesCommission = new CommissionSchedule(0m, 0m, 0m),
            FxSpreadRatio = 0m,
            MinimumExpectedProfitMultiple = 2m,
            CostLimits = new MonthlyCostLimits(20_000m, 15_000m, 5_000m, 0m),
        };

        a.UnitedStatesSellRegulatoryFees.Should().Be(TradingAssumptionsDefaults.UnitedStatesSellRegulatoryFees);
    }

    // FR-17, §4, #358, IADR-0173: 計画確定値は **2**（利用者決定 2026-07-23・「往復費用＋税の 2 倍」）。
    // 旧値 1.5 は計画確定前の暫定値（当時の計画は未確定の <1.5 倍>）であり、実装が追随していなかった。
    [Fact]
    public void 最小期待利益倍率は計画確定値の2()
    {
        TradingAssumptionsDefaults.Create().MinimumExpectedProfitMultiple.Should().Be(2m);
    }
}
