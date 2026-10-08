using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using ReportService.Domain;
using Xunit;

namespace ReportService.Tests;

// FR-06, UC-03〜05, 計画 ADR-0053 決定 2・フォローアップ 3, #1224, IADR-0516: 監査台帳から引いた記録をセッションの窓で絞る（純関数）。
//
// 暦の基準: 2026-10-05 は月曜。日報 daily-2026-10-06 の窓は (10-05 16:00 JST, 10-06 16:00 JST]＝米国 ET 10-05・東証 JST 10-06 のセッション。
// 米国の ET 10-05 のセッションは JST 10-05 22:30〜10-06 05:00（夏時間）。米国の夏時間は 2026-11-01（日）に終わる。
public class ReportLedgerWindowingTests
{
    private static readonly ReportScheduleOptions Defaults = new();

    private static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    private static DateTimeOffset Et(int month, int day, int hour, int minute = 0)
    {
        var local = new DateTime(2026, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Eastern.GetUtcOffset(local));
    }

    private static DateTimeOffset Jst(int month, int day, int hour, int minute = 0) =>
        new(new DateTime(2026, month, day, hour, minute, 0, DateTimeKind.Unspecified), TimeSpan.FromHours(9));

    private static ReportSessionWindow WindowOf(ReportKind kind, DateOnly start, ReportScheduleOptions? options = null)
    {
        var o = options ?? Defaults;
        return ReportSchedule.SessionWindowOf(ReportSchedule.PeriodOf(kind, start, o), o);
    }

    private static ReportSessionWindow Daily(int month, int day) => WindowOf(ReportKind.Daily, new DateOnly(2026, month, day));

    private static readonly DateOnly[] JpHolidays2026Autumn =
    [
        new(2026, 9, 21), new(2026, 9, 22), new(2026, 9, 23), new(2026, 10, 12), new(2026, 11, 3), new(2026, 11, 23),
    ];

    // T-06-062, FR-06, #1224, IADR-0516 決定 1: 市場を持つ記録は「セッションの大引けと記録の時刻の遅いほう」が窓に入る日報に載る。
    // 米国のセッション中・寄り付き前・閉場後の記録は約定と同じ日報（ET 10-05 → 日報 10-06）、東証の生成境界の後の記録は翌日報。
    [Theory]
    [InlineData("米国のセッション中（ET 10-05 10:00）", 0, 10, 5, 10, 6)]
    [InlineData("米国の寄り付き前（ET 10-05 02:00＝JST 10-05 15:00）", 0, 10, 5, 2, 6)]
    [InlineData("米国の閉場後（ET 10-05 18:00＝JST 10-06 07:00）", 0, 10, 5, 18, 6)]
    [InlineData("東証のセッション中（JST 10-06 10:00）", 1, 10, 6, 10, 6)]
    [InlineData("東証の大引け後・生成境界の前（JST 10-06 15:45）", 1, 10, 6, 15, 6)]
    [InlineData("東証の生成境界の後（JST 10-06 17:00）", 1, 10, 6, 17, 7)]
    public void T06_062_市場を持つ記録は大引けと記録時刻の遅いほうが入る日報に載る(
        string label, int marketIndex, int month, int day, int hour, int expectedDailyDay)
    {
        var market = marketIndex == 0 ? Market.UnitedStates : Market.Japan;
        var minute = market == Market.Japan && hour == 15 ? 45 : 0;
        var at = market == Market.UnitedStates ? Et(month, day, hour, minute) : Jst(month, day, hour, minute);

        var hits = new[] { 5, 6, 7 }.Where(d => Daily(10, d).Counts(market, at)).ToList();

        hits.Should().Equal([expectedDailyDay], label);
        // セッション中の記録は約定の絞り込み（Includes）と同じ日報に入る（約定と揃う）。
        if (hour is 10)
            Daily(10, expectedDailyDay).Includes(market, at).Should().BeTrue(label);
    }

    // T-06-063, FR-06, #1224, IADR-0516 決定 1（否定形・ちょうど 1 つ）: 2026-09〜11（東証の祝日・米国の夏時間の終わりを含む）の
    // 連続する営業日の日報の窓で、市場を持つ記録・持たない記録のどれもが日報のちょうど 1 つに入る。月報の窓はその月の日報の窓の和に一致する。
    [Fact]
    public void T06_063_どの記録も生成される日報のちょうど1つに入り_月報は日報の和に一致する()
    {
        var options = new ReportScheduleOptions { Holidays = new HashSet<DateOnly>(JpHolidays2026Autumn) };
        var businessDays = new List<DateOnly>();
        for (var d = new DateOnly(2026, 9, 1); d <= new DateOnly(2026, 11, 30); d = d.AddDays(1))
        {
            if (ReportSchedule.IsBusinessDay(d, options))
                businessDays.Add(d);
        }

        var dailies = businessDays.Select(d => (Day: d, Window: WindowOf(ReportKind.Daily, d, options))).ToList();
        var first = dailies[0].Window.ClosedAfter;
        var last = dailies[^1].Window.ClosedUntil;
        var october = WindowOf(ReportKind.Monthly, new DateOnly(2026, 10, 1), options);

        var checkedCount = 0;
        for (var at = first.AddHours(-30); at <= last.AddHours(30); at = at.AddMinutes(37))
        {
            foreach (Market? market in new Market?[] { Market.UnitedStates, Market.Japan, null })
            {
                var reportable = market is { } m ? ReportSessionWindow.ReportableAt(m, at) : at;
                if (reportable <= first || reportable > last)
                    continue;

                var hits = dailies.Where(x => market is { } mk ? x.Window.Counts(mk, at) : x.Window.Contains(at)).ToList();
                hits.Should().HaveCount(1, $"{market?.ToString() ?? "市場なし"} の記録 {at:o}");

                var inOctoberDailies = hits[0].Day >= new DateOnly(2026, 10, 1) && hits[0].Day <= new DateOnly(2026, 10, 30);
                var inOctoberMonthly = market is { } mo ? october.Counts(mo, at) : october.Contains(at);
                inOctoberMonthly.Should().Be(inOctoberDailies, $"{market?.ToString() ?? "市場なし"} の記録 {at:o}");
                checkedCount++;
            }
        }

        checkedCount.Should().BeGreaterThan(9_000);
    }

    // T-06-064, FR-06, ADR-0027 決定 3, #1224, IADR-0516 決定 2: 借株料は計上日（市場の現地の日）のセッションで帰属する。
    // 週末（ET 土曜）の計上日は月曜の日報に、ET 10-05 の計上（翌未明の記録）は日報 10-06 に、東証の夜の記録は翌日報に入る。
    [Fact]
    public void T06_064_借株料は計上日のセッションで帰属し_週末の計上日は月曜の日報に入る()
    {
        var saturday = new BorrowFeeAccrued("TSLA", Market.UnitedStates, new DateOnly(2026, 10, 3), 0.05m, 1_000m, 0.137m, Et(10, 3, 17));
        var monday = new BorrowFeeAccrued("TSLA", Market.UnitedStates, new DateOnly(2026, 10, 5), 0.05m, 1_000m, 0.137m, Et(10, 6, 1));
        var unavailable = new BorrowFeeAccrualUnavailable("TSLA", Market.UnitedStates, new DateOnly(2026, 10, 5), "料率が取れない", Et(10, 5, 20));
        var jpEvening = new BorrowFeeAccrued("7203", Market.Japan, new DateOnly(2026, 10, 6), 0.011m, 2_000m, 0.06m, Jst(10, 6, 18));
        var record = new BorrowFeeRecord([saturday, monday, jpEvening], [unavailable]);

        var dailyMon = record.Within(Daily(10, 5));
        var dailyTue = record.Within(Daily(10, 6));
        var dailyWed = record.Within(Daily(10, 7));

        dailyMon.Accruals.Should().Equal(saturday);
        dailyMon.Unavailable.Should().BeEmpty();
        dailyTue.Accruals.Should().Equal(monday);
        dailyTue.Unavailable.Should().Equal(unavailable);
        dailyWed.Accruals.Should().Equal(jpEvening);
    }

    private static OrderApproved Approval(Guid id, Market market, DateTimeOffset at, StopLossExecutionMethod method) =>
        new(id, new OrderIntent("AAPL", market, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 1, 100m),
            1, at, StopLossMethod: method);

    // T-06-065, FR-06, FR-10, ADR-0040 決定 1, #1224, IADR-0516 決定 2・4: 損切りの手法（承認時点）は承認の市場・時刻で絞り、件数を引き直す。
    // 米国の 1 セッション（ET 10-05）の承認は JST の 2 日（10-05 23:00・10-06 02:00）に跨っても 1 日と数える。市場を持たない旧い明細と
    // 件数だけの値は絞らない。
    [Fact]
    public void T06_065_損切りの承認は窓で絞り件数を引き直し_日はセッションの日で数える()
    {
        var a1 = Guid.NewGuid();
        var a2 = Guid.NewGuid();
        var next = Guid.NewGuid();
        var usage = StopLossMethodUsage.From(
        [
            Approval(a1, Market.UnitedStates, Jst(10, 5, 23), StopLossExecutionMethod.BrokerStopOrder),
            Approval(a2, Market.UnitedStates, Jst(10, 6, 2), StopLossExecutionMethod.SoftwareStop),
            Approval(next, Market.UnitedStates, Et(10, 6, 10), StopLossExecutionMethod.BrokerStopOrder),
        ], unreadableCount: 1);

        var within = usage.Within(Daily(10, 6));

        within.Approvals.Select(a => a.DecisionId).Should().Equal(a1, a2);
        within.Counts.Should().Equal(
            new StopLossMethodCount(StopLossExecutionMethod.BrokerStopOrder, 1),
            new StopLossMethodCount(StopLossExecutionMethod.SoftwareStop, 1));
        within.UnreadableCount.Should().Be(1);

        var comparison = StopLossMethodComparison.From(within, new StopLossMethodResolutionFeed([]));
        comparison.ApprovalDays.Should().Be(1);
        comparison.Outcomes.Select(o => o.Day).Distinct().Should().Equal(new DateOnly(2026, 10, 5));

        // 市場を持たない旧い明細は絞らず、件数だけの値（明細なし）はそのまま返す。
        var legacy = new StopLossMethodUsage([new StopLossMethodCount(StopLossExecutionMethod.BrokerStopOrder, 1)], 0)
        {
            Approvals = [new StopLossMethodApproval(Guid.NewGuid(), StopLossExecutionMethod.BrokerStopOrder, Et(10, 9, 10))],
        };
        legacy.Within(Daily(10, 6)).Approvals.Should().HaveCount(1);
        var countsOnly = new StopLossMethodUsage([new StopLossMethodCount(StopLossExecutionMethod.SoftwareStop, 2)], 0);
        countsOnly.Within(Daily(10, 6)).Should().BeSameAs(countsOnly);
    }

    // T-06-066, FR-06, FR-10, ADR-0022 決定 1・2, #1224, IADR-0516 決定 2: 為替の情報源の状態は発生時刻で絞り（市場を持たない）、
    // 鮮度切れのレートでの決済は約定と同じ形（市場・時刻）で数える。クレジットは残った記録から引き直す。
    [Fact]
    public void T06_066_為替の状態は発生時刻で絞りクレジットを引き直す()
    {
        var fellBackIn = new FxRateSourceFellBack("USDJPY", "fred", 2, 2, Jst(10, 5, 23));
        var restoredIn = new FxRateSourcePrimaryRestored("USDJPY", FxSourceCredits.BojSourceName, Jst(10, 5, 23), Jst(10, 6, 9));
        var bojUsageBefore = new FxRateSourceUsed("USDJPY", FxSourceCredits.BojSourceName, 1, 2, Jst(10, 5, 15));
        var fredUsageIn = new FxRateSourceUsed("USDJPY", "fred", 2, 2, Jst(10, 6, 0, 30));
        var staleAfter = new FxRateStale("USDJPY", Jst(9, 28, 0), 8.5, 5, 30, Jst(10, 6, 17));
        var staleCloseIn = new PositionClosedWithStaleFxRate("AAPL", Market.UnitedStates, "USDJPY", 10, 150m, Jst(9, 28, 0), 7.5, Et(10, 5, 15));
        var status = FxSourceStatus.Compose([fellBackIn], [restoredIn], [staleAfter], [staleCloseIn], [bojUsageBefore, fredUsageIn]);

        var withoutRestoration = FxSourceStatus.Compose([fellBackIn], [], [staleAfter], [staleCloseIn], [bojUsageBefore, fredUsageIn])
            .Within(Daily(10, 6));
        var within = status.Within(Daily(10, 6));

        within.FellBacks.Should().Equal(fellBackIn);
        within.Restorations.Should().Equal(restoredIn);
        within.StaleWarnings.Should().BeEmpty();
        within.StaleCloses.Should().Equal(staleCloseIn);
        within.Usages.Should().Equal(fredUsageIn);
        within.PrimarySourceCredits.Should().Equal(FxSourceCredits.Boj);
        // 窓の外の日銀の使用記録だけが日銀を使った証拠なら、絞った後のクレジットは空（使っていない源のクレジットを出さない）。
        withoutRestoration.PrimarySourceCredits.Should().BeEmpty();
        status.Within(Daily(10, 7)).StaleWarnings.Should().Equal(staleAfter);
    }

    // T-06-062（自動縮小）, FR-06, FR-10, UC-06, #1224, IADR-0516 決定 2: 自動縮小 1 回は明細の市場ごとの報告可能になる瞬間の最も早いもので
    // 1 つの日報に入る（市場が混ざっても 2 つへ割れない）。明細が無ければ執行の時刻で数える。強制買戻しの推定は市場・推定時刻で数える。
    [Fact]
    public void T06_062_自動縮小と強制買戻しの推定もセッションの窓で1つの日報に入る()
    {
        var usDuringSession = new MaintenanceMarginReductionExecuted(
            Guid.NewGuid(), 1.2m, 1.3m, 1.5m, 1.6m,
            [new MaintenanceMarginReductionItem("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.MarginLong, 5, 200m, 300m)],
            Et(10, 5, 11));
        var mixed = new MaintenanceMarginReductionExecuted(
            Guid.NewGuid(), 1.2m, 1.3m, 1.5m, null,
            [
                new MaintenanceMarginReductionItem("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.MarginLong, 5, 200m, 300m),
                new MaintenanceMarginReductionItem("7203", Market.Japan, TradeSide.Buy, ProductType.MarginLong, 100, 20m, 300m),
            ],
            Jst(10, 6, 10));
        var noItems = new MaintenanceMarginReductionExecuted(Guid.NewGuid(), 1.2m, 1.3m, 1.5m, null, [], Jst(10, 6, 17));
        IReadOnlyList<MaintenanceMarginReductionExecuted> reductions = [usDuringSession, mixed, noItems];

        reductions.Within(Daily(10, 6)).Should().Equal(usDuringSession, mixed);
        reductions.Within(Daily(10, 7)).Should().Equal(noItems);
        new[] { 5, 6, 7, 8 }.Sum(d => reductions.Within(Daily(10, d)).Count).Should().Be(3);

        var buyIn = new BuyInInferred(Guid.NewGuid(), "GME", Market.UnitedStates, -10, 0, 0, 10, 10, [], new DateOnly(2026, 11, 4),
            Et(10, 5, 19), Et(10, 5, 19, 5));
        IReadOnlyList<BuyInInferred> inferences = [buyIn];
        inferences.Within(Daily(10, 5)).Should().BeEmpty();
        inferences.Within(Daily(10, 6)).Should().Equal(buyIn);
    }

    // T-06-070, FR-06, #1224, IADR-0516 決定 3: 供給元へ渡す照会の範囲は窓を覆う JST の暦日の外包（下端は窓の最初の現地取引日・
    // ClosedAfter の JST 日付・期間の始まりの最小、上端は期間の終わり）。月曜の日報は金曜〜月曜を引く。
    [Fact]
    public void T06_070_監査台帳の照会範囲は窓を覆うJSTの暦日の外包()
    {
        Daily(10, 6).JstLedgerRange(new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 6))
            .Should().Be((new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 6)));
        Daily(10, 5).JstLedgerRange(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 5))
            .Should().Be((new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 5)));
        WindowOf(ReportKind.Monthly, new DateOnly(2026, 10, 1)).JstLedgerRange(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 30))
            .Should().Be((new DateOnly(2026, 9, 30), new DateOnly(2026, 10, 30)));
    }
}
