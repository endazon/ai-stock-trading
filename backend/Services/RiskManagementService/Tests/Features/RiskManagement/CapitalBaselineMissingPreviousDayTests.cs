using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Metrics;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Persistence;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, UC-06, #1423 M-3, ADR-0073 決定1・2, IADR-0581 決定1:
// **基準資金（equity）の「照会できない」は、直前の米国東部暦日に成功した観測が無いことを指す。そのときは新規建てを拒否する。**
//
// 計画の逐語（ADR-0073 決定1）:「判定する時点の米国東部（ET）暦日の、直前の暦日に、成功した観測が 1 件も無いことを
// 『照会できない』とする」「暦日で数える。営業日では数えない」「解除は自動である」。
public class CapitalBaselineMissingPreviousDayTests
{
    // 2026-07-13 は月曜。14:00 ET（夏時間）＝ 18:00Z。
    private static readonly DateTimeOffset MondayAfternoon = new(2026, 7, 13, 18, 0, 0, TimeSpan.Zero);

    // 各暦日の 23:55 ET（夏時間）＝ 翌 03:55Z。巡回の最後の観測を模す。
    private static DateTimeOffset LateEastern(int year, int month, int day) =>
        new DateTimeOffset(year, month, day, 3, 55, 0, TimeSpan.Zero).AddDays(1);

    private static RiskManagementDbContext NewContext() =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static EfCapitalBaselineStore NewStore(RiskManagementDbContext db, FakeClock clock, BusinessMetrics? metrics = null) =>
        new(db, clock, new CapitalBaselineOptions(), NullLogger<EfCapitalBaselineStore>.Instance, metrics ?? new BusinessMetrics());

    private static OrderIntent Entry() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 1, 100m, PositionEffect.Open);

    private static OrderIntent Close() =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.InternalPaper, 1, 100m, PositionEffect.Close);

    private static PortfolioSnapshot Snapshot(decimal? capital) => new()
    {
        Capital = capital,
        OpenPositionCount = 1,
        InvestedCapital = 0m,
        DailyOrderedAmount = 0m,
        DailyRealizedPnl = 0m,
        UnrealizedPnl = 0m,
    };

    // 🔴 T-10-3035（境界の両側・暦日で数える）: 月曜の判定は**日曜の観測**を求める。営業日（金曜）では数えない。
    [Theory]
    [InlineData(12, true)]  // 日曜（直前の暦日）の観測がある → 供給
    [InlineData(11, false)] // 土曜が最後 → 日曜が欠けている → 未供給
    [InlineData(10, false)] // 金曜（直前の営業日）が最後 → 未供給（営業日では数えない）
    public void 月曜の判定は日曜の観測を求める_営業日では数えない(int lastObservedDay, bool supplied)
    {
        using var db = NewContext();
        var store = NewStore(db, new FakeClock(MondayAfternoon, new DateOnly(2026, 7, 13)));

        store.Record(3_000m, LateEastern(2026, 7, lastObservedDay));

        (store.GetCurrent() is not null).Should().Be(supplied);
    }

    // T-10-3036: 照会できないときは**新規建てだけ**を拒否し、手仕舞いは止めない（ADR-0073 決定2・ADR-0041 決定2）。
    [Fact]
    public void 直前の暦日の観測が無ければ新規建ては拒否し手仕舞いは通す()
    {
        using var db = NewContext();
        var store = NewStore(db, new FakeClock(MondayAfternoon, new DateOnly(2026, 7, 13)));
        store.Record(3_000m, LateEastern(2026, 7, 10)); // 金曜が最後

        var capital = store.GetCurrent()?.EquityInBase;

        RiskEvaluator.Evaluate(Entry(), TradingDefaults.CreateSettings(), Snapshot(capital)).Reasons
            .Should().Contain(RejectionReason.CapitalBaselineUnavailable);
        RiskEvaluator.Evaluate(Close(), TradingDefaults.CreateSettings(), Snapshot(capital)).IsApproved
            .Should().BeTrue("手仕舞い（Close）と損切りは止めない");
    }

    // T-10-3037（解除は自動）: 欠けていた暦日の翌日に観測が成功すれば、次の暦日の読み出しから供給に戻る。利用者の操作は要らない。
    [Fact]
    public void 直前の暦日の観測が戻れば利用者の操作なしに供給へ戻る()
    {
        using var db = NewContext();
        var clock = new FakeClock(MondayAfternoon, new DateOnly(2026, 7, 13));
        var store = NewStore(db, clock);
        store.Record(3_000m, LateEastern(2026, 7, 11)); // 土曜が最後（日曜が欠け）
        store.GetCurrent().Should().BeNull();

        // 月曜の観測が成功し（当日の行は月曜の判定には使わない）、火曜になる。
        store.Record(3_100m, LateEastern(2026, 7, 13));
        clock.UtcNow = MondayAfternoon.AddDays(1);

        store.GetCurrent()!.EquityInBase.Should().Be(3_100m);
    }

    // T-10-3038: 表示・通知の口（Read）は審査と**同じ判定**を返し、**計器を動かさない**（帰結を数えるのは審査の側だけ）。
    [Fact]
    public void Readは同じ判定を返し計器を動かさない()
    {
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        using var db = NewContext();
        var store = NewStore(db, new FakeClock(MondayAfternoon, new DateOnly(2026, 7, 13)), metrics);
        store.Record(3_000m, LateEastern(2026, 7, 10));

        var reading = store.Read();

        reading.Baseline.Should().BeNull();
        reading.Outcome.Should().Be(CapitalBaselineReadOutcome.UnavailableMissingPreviousDay);
        reading.ObservationMissing.Should().BeTrue();
        reading.LatestTradingDay.Should().Be(new DateOnly(2026, 7, 10));
        reading.GapDays.Should().Be(3);
        capture.TagValuesOf(BusinessMetricNames.RiskCapitalBaselineReads, BusinessMetricNames.TagOutcome)
            .Should().BeEmpty("Read は表示・通知用であり、審査の帰結の計数を水増ししない");
    }

    // T-10-3039（否定形）: 「観測の欠け」に**数えない**帰結 —— 行が 1 つも無い（初回の配備）と人手の 0（意図した停止）。
    // 欠けの通知（ADR-0073 決定3）はこの 2 つで鳴らさない。
    [Fact]
    public void 行なしと人手の0は観測の欠けに数えない()
    {
        using var db = NewContext();
        var store = NewStore(db, new FakeClock(MondayAfternoon, new DateOnly(2026, 7, 13)));
        store.Read().ObservationMissing.Should().BeFalse("行なしは初回の配備");

        store.Record(0m, LateEastern(2026, 7, 12));
        store.Read().Outcome.Should().Be(CapitalBaselineReadOutcome.UnavailableNonPositive);
        store.Read().ObservationMissing.Should().BeFalse("人手の 0 は意図した停止");
    }

    // T-10-3048（プロパティベース）: どの時刻に判定しても（夏時間の切り替えを含む 1 年・97 分刻み）、
    // 直前の米国東部暦日の行があれば供給し、その 1 日前（2 暦日前）までしか無ければ未供給である。
    [Fact]
    public void どの時刻でも_直前の暦日の行の有無だけで供給が決まる()
    {
        var broken = new List<string>();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var now = from; now < from.AddDays(366); now = now.AddMinutes(97))
        {
            var previous = RiskWorkerWebApplicationFactory.PreviousTradingDayObservation(now);
            var twoBefore = RiskWorkerWebApplicationFactory.PreviousTradingDayObservation(previous);

            using var withPrevious = NewContext();
            var supplied = NewStore(withPrevious, new FakeClock(now, DateOnly.FromDateTime(now.UtcDateTime)));
            supplied.Record(3_000m, previous);
            if (supplied.GetCurrent() is null)
                broken.Add($"{now:o} 直前の暦日の行があるのに未供給");

            using var withGap = NewContext();
            var gap = NewStore(withGap, new FakeClock(now, DateOnly.FromDateTime(now.UtcDateTime)));
            gap.Record(3_000m, twoBefore);
            if (gap.GetCurrent() is not null)
                broken.Add($"{now:o} 2 暦日前の行しか無いのに供給");
        }

        broken.Should().BeEmpty();
    }
}
