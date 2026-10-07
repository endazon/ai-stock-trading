using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using RiskManagementService.Features.RiskManagement;
using Xunit;

namespace RiskManagementService.Tests;

// T-06-059, FR-06, FR-16, #1186, IADR-0506 決定 2: 取引日の条件を約定時刻（UTC）の範囲へ写す外包は、
// 市場の現地取引日の判定（TradingDay.Of）の**上位集合**である（取りこぼさない）。
public class LedgerScanBoundsTests
{
    public static TheoryData<Market, DateOnly> Windows => new()
    {
        // 米国の夏時間の開始（2026-03-08）・終了（2026-11-01）・平常日。東証は夏時間なし。
        { Market.UnitedStates, new DateOnly(2026, 3, 8) },
        { Market.UnitedStates, new DateOnly(2026, 11, 1) },
        { Market.UnitedStates, new DateOnly(2026, 10, 5) },
        { Market.Japan, new DateOnly(2026, 3, 8) },
        { Market.Japan, new DateOnly(2026, 11, 1) },
        { Market.Japan, new DateOnly(2026, 10, 5) },
    };

    [Theory]
    [MemberData(nameof(Windows))]
    public void T06_059_取引日の判定で入る瞬間は_外包の範囲にも必ず入る(Market market, DateOnly center)
    {
        // 中心日の前後 4 日を 15 分刻みで走査し、前後 3 日の各取引日について 3 種の外包を確かめる。
        var violations = new List<string>();
        var start = new DateTimeOffset(center.AddDays(-4).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        for (var instant = start; instant < start.AddDays(8); instant = instant.AddMinutes(15))
        {
            var tradingDay = PortfolioProjection.TradeDate(instant, market);
            for (var d = center.AddDays(-3); d <= center.AddDays(3); d = d.AddDays(1))
            {
                if (tradingDay < d && !(instant < LedgerScanBounds.ExecutedBeforeForTradingDayBefore(d)!.Value))
                    violations.Add($"{instant:O}: 取引日 {tradingDay} < {d} なのに before の外包の外");
                if (tradingDay >= d && !(instant >= LedgerScanBounds.ExecutedAtOrAfterForTradingDayFrom(d)!.Value))
                    violations.Add($"{instant:O}: 取引日 {tradingDay} >= {d} なのに from の外包の外");
                if (tradingDay <= d && !(instant < LedgerScanBounds.ExecutedBeforeForTradingDayTo(d)!.Value))
                    violations.Add($"{instant:O}: 取引日 {tradingDay} <= {d} なのに to の外包の外");
            }
        }

        violations.Should().BeEmpty();
    }

    [Fact]
    public void T06_059_外包は_UTC_の暦日の0時に前後1日の余裕を足した値である()
    {
        var d = new DateOnly(2026, 10, 5);

        LedgerScanBounds.ExecutedBeforeForTradingDayBefore(d).Should().Be(new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero));
        LedgerScanBounds.ExecutedAtOrAfterForTradingDayFrom(d).Should().Be(new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero));
        LedgerScanBounds.ExecutedBeforeForTradingDayTo(d).Should().Be(new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero));
        LedgerScanBounds.ExecutedBeforeForTradingDayBefore(d)!.Value.Offset.Should().Be(TimeSpan.Zero, "Npgsql の timestamptz の引数はオフセット 0 を要する");
    }

    [Fact]
    public void T06_059_DateOnly_の端で溢れる側は境界を外して無制限にする()
    {
        LedgerScanBounds.ExecutedAtOrAfterForTradingDayFrom(DateOnly.MinValue).Should().BeNull("下限なし＝従来の全行読みと同じ側");
        LedgerScanBounds.ExecutedBeforeForTradingDayBefore(DateOnly.MaxValue).Should().BeNull();
        LedgerScanBounds.ExecutedBeforeForTradingDayTo(DateOnly.MaxValue).Should().BeNull();
        LedgerScanBounds.ExecutedBeforeForTradingDayTo(DateOnly.MaxValue.AddDays(-1)).Should().BeNull();

        LedgerScanBounds.ExecutedBeforeForTradingDayBefore(DateOnly.MinValue).Should().NotBeNull();
        LedgerScanBounds.ExecutedAtOrAfterForTradingDayFrom(DateOnly.MinValue.AddDays(1)).Should().NotBeNull();
        LedgerScanBounds.ExecutedBeforeForTradingDayTo(DateOnly.MaxValue.AddDays(-2)).Should().NotBeNull();
    }
}
