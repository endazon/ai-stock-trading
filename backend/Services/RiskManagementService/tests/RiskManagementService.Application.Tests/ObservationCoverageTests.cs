using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Services;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-21, FR-10, FR-06, #463, IADR-0181: **報告期間が観測の届いた取引日で覆われているか**の判定。
//
// 計画（FR-21・2026-08-08 改定・裁定 project-planning#292）が名指しした 2 つの失敗モードを塞ぐ。
//   1. **初回観測より前の期間**（初回観測が 8/20 で 7 月分の月報を生成する）
//   2. **観測が途中で止まった期間**（ブローカ接続の障害等）
//
// 従前の「最終観測時刻が非 null なら供給」では**どちらも素通りしていた**。
public class ObservationCoverageTests
{
    private static readonly IBusinessCalendarStub Calendar = new();

    // 2026-08-03(月) 〜 2026-08-07(金) の 5 営業日。
    private static readonly DateOnly Mon = new(2026, 8, 3);
    private static readonly DateOnly Fri = new(2026, 8, 7);

    private static List<DateOnly> WeekDays() =>
        [Mon, Mon.AddDays(1), Mon.AddDays(2), Mon.AddDays(3), Fri];

    [Fact]
    public void 期間の営業日がすべて観測されていれば覆っている()
    {
        ObservationCoverage.Covers(WeekDays(), Mon, Fri, Calendar).Should().BeTrue();
    }

    // **失敗モード 1**: 初回観測より前の期間。観測は 1 日も無い。
    [Fact]
    public void 観測が一度も無い期間は覆っていない()
    {
        ObservationCoverage.Covers([], Mon, Fri, Calendar).Should().BeFalse(
            "初回観測より前の期間が「正当な 0」として報告されてはならない");
    }

    // **失敗モード 2（改定の核心）**: 途中で止まった期間。**1 日でも欠ければ覆っていない。**
    [Theory]
    [InlineData(0)]  // 初日が欠ける
    [InlineData(2)]  // 中日が欠ける
    [InlineData(4)]  // 最終日が欠ける
    public void 営業日が1日でも欠ければ覆っていない(int missingIndex)
    {
        var days = WeekDays();
        days.RemoveAt(missingIndex);

        ObservationCoverage.Covers(days, Mon, Fri, Calendar).Should().BeFalse(
            "観測が途中で止まった期間が「正当な 0」として報告されてはならない");
    }

    // 週末は営業日ではないため、観測が無くても覆っている判定を妨げない。
    [Fact]
    public void 週末に観測が無くても営業日が揃っていれば覆っている()
    {
        // 期間を日曜（8/2）〜土曜（8/8）へ広げても、営業日は月〜金の 5 日だけである。
        ObservationCoverage.Covers(WeekDays(), Mon.AddDays(-1), Fri.AddDays(1), Calendar)
            .Should().BeTrue();
    }

    // **否定形**: 営業日が 1 日も無い期間は覆えない。
    // ここを true にすると、**観測が一度も無くても**週末だけの期間が「正当な 0」になる。
    [Fact]
    public void 営業日を含まない期間は覆っていない()
    {
        var saturday = new DateOnly(2026, 8, 8);
        var sunday = new DateOnly(2026, 8, 9);

        ObservationCoverage.Covers([], saturday, sunday, Calendar).Should().BeFalse();
    }

    // 逆順の期間は覆っていないものとして扱う（呼び出し側が 400 で弾く前提の二重防御）。
    [Fact]
    public void 逆順の期間は覆っていない()
    {
        ObservationCoverage.Covers(WeekDays(), Fri, Mon, Calendar).Should().BeFalse();
    }

    // 期間外の観測日は判定に寄与しない（余分に観測されていても不足は埋まらない）。
    [Fact]
    public void 期間外の観測日は不足を埋めない()
    {
        var days = WeekDays();
        days.RemoveAt(2);
        days.Add(Fri.AddDays(7));

        ObservationCoverage.Covers(days, Mon, Fri, Calendar).Should().BeFalse();
    }

    // 週末のみを営業日から外す暦（本番の WeekendBusinessCalendar と同じ規則）。
    private sealed class IBusinessCalendarStub : AiStockTrading.RiskManagement.Application.Ports.IBusinessCalendar
    {
        public DateOnly NextBusinessDay(DateOnly date)
        {
            var next = date.AddDays(1);
            while (!IsBusinessDay(next))
            {
                next = next.AddDays(1);
            }

            return next;
        }

        public bool IsBusinessDay(DateOnly date) =>
            date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
    }
}
