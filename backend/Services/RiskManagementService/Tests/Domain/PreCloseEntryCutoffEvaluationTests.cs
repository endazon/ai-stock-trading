using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// T-10-2921, T-10-2922, FR-10, FR-19, #1396, IADR-0561 決定 2: 判定コア（RiskEvaluator）が閉場前の締め切りの供給で新規建て（買い増し・売り増しを含む）を
// PreCloseEntryCutoff で止め、手仕舞いは止めないこと。境界（N 分前ちょうど・1 分前・閉場の後・休場日）を固定する。
// 閉場の求め方は MarketHoursSessionCloseTests（共有カーネル）、審査のサービスの組み立ては PreCloseEntryCutoffScreeningTests が見る。
public class PreCloseEntryCutoffEvaluationTests
{
    // 2026-10-19（月）米国東部の夏時間。閉場 16:00 ET＝20:00 UTC。
    private static readonly DateTimeOffset Close = new(2026, 10, 19, 20, 0, 0, TimeSpan.Zero);

    private static OrderIntent Intent(TradeSide side, PositionEffect effect, ProductType productType = ProductType.Cash) =>
        new("MARA", Market.UnitedStates, side, productType, BrokerProvider.InternalPaper, 10, 20m, effect);

    private static PortfolioSnapshot Snapshot() => new() { Capital = 100_000m };

    private static RiskManagementSettings ShortEnabledSettings() => TradingDefaults.CreateSettings() with
    {
        Guard = TradingDefaults.CreateGuardSettings() with
        {
            EnabledProductTypes = new HashSet<ProductType> { ProductType.Cash, ProductType.MarginLong, ProductType.ShortSell },
        },
        Stage = new StageSettings(AiStockTrading.Shared.Kernel.Trading.TradingStage.Stage3ScaledLive, BrokerProvider.InternalPaper, CapitalCapRatio: 1_000_000m),
    };

    private static OrderScreeningResult Evaluate(
        OrderIntent intent, PreCloseEntryCutoffSupply? cutoff, RiskManagementSettings? settings = null) =>
        RiskEvaluator.Evaluate(intent, settings ?? TradingDefaults.CreateSettings(), Snapshot(), preCloseCutoff: cutoff);

    private static PreCloseEntryCutoffSupply At(DateTimeOffset now, DateTimeOffset? close = null, int minutes = 60) =>
        new(now, close ?? Close, minutes);

    [Fact]
    public void T_10_2921_閉場の60分前以降の買いの新規建ては締め切りで拒否される()
    {
        var result = Evaluate(Intent(TradeSide.Buy, PositionEffect.Open), At(Close.AddMinutes(-58)));

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().ContainSingle().Which.Should().Be(RejectionReason.PreCloseEntryCutoff);
    }

    [Fact]
    public void T_10_2921_売りの新規建て空売りも締め切りで拒否される()
    {
        Evaluate(Intent(TradeSide.Sell, PositionEffect.Open, ProductType.ShortSell), At(Close.AddMinutes(-30)), ShortEnabledSettings())
            .Reasons.Should().Contain(RejectionReason.PreCloseEntryCutoff);
    }

    // 🔴 買い増し・売り増しも新規建て（建玉効果 Open）であり、保有の有無で締め切りを変えない（裁定「含める」）。
    [Fact]
    public void T_10_2921_保有中の買い増しも締め切りで拒否される()
    {
        var snapshot = new PortfolioSnapshot { Capital = 100_000m, OpenPositionCount = 1, InvestedCapital = 200m };

        RiskEvaluator.Evaluate(
                Intent(TradeSide.Buy, PositionEffect.Open), TradingDefaults.CreateSettings(), snapshot,
                preCloseCutoff: At(Close.AddMinutes(-10)))
            .Reasons.Should().Contain(RejectionReason.PreCloseEntryCutoff);
    }

    // 🔴 手仕舞い（Close）は止めない（ADR-0009）。
    [Theory]
    [InlineData(TradeSide.Sell)]
    [InlineData(TradeSide.Buy)]
    public void T_10_2921_手仕舞いは締め切りの中でも止めない(TradeSide side)
    {
        Evaluate(Intent(side, PositionEffect.Close), At(Close.AddMinutes(-5))).IsApproved.Should().BeTrue();
    }

    [Theory]
    [InlineData(-61, false)] // 61 分前: 締め切りの前
    [InlineData(-60, true)]  // ちょうど 60 分前: 締め切りに入る（「以降」）
    [InlineData(-1, true)]
    [InlineData(0, true)]    // 閉場ちょうど
    [InlineData(120, true)]  // 閉場の後（同じ現地の日）も入る
    public void T_10_2922_境界は閉場のN分前ちょうどから入る(int minutesFromClose, bool rejected)
    {
        var result = Evaluate(Intent(TradeSide.Buy, PositionEffect.Open), At(Close.AddMinutes(minutesFromClose)));

        result.Reasons.Contains(RejectionReason.PreCloseEntryCutoff).Should().Be(rejected);
    }

    [Fact]
    public void T_10_2922_閉場が無い休場日は締め切りに入らない()
    {
        var supply = new PreCloseEntryCutoffSupply(Close, SessionClose: null, Minutes: 60);

        Evaluate(Intent(TradeSide.Buy, PositionEffect.Open), supply).IsApproved.Should().BeTrue();
    }

    [Fact]
    public void T_10_2922_分数は供給の値を使う()
    {
        // 30 分の構成なら 45 分前は締め切りの外、20 分前は中。
        Evaluate(Intent(TradeSide.Buy, PositionEffect.Open), At(Close.AddMinutes(-45), minutes: 30)).IsApproved.Should().BeTrue();
        Evaluate(Intent(TradeSide.Buy, PositionEffect.Open), At(Close.AddMinutes(-20), minutes: 30))
            .Reasons.Should().Contain(RejectionReason.PreCloseEntryCutoff);
    }

    [Fact]
    public void T_10_2922_供給が無ければ評価しない()
    {
        Evaluate(Intent(TradeSide.Buy, PositionEffect.Open), cutoff: null).IsApproved.Should().BeTrue();
    }
}
