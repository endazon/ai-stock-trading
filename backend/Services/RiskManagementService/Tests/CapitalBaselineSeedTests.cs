using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using RiskManagementService.Common.Abstractions;
using RiskManagementService.Infrastructure.Persistence;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, #905, ADR-0041 決定2, IADR-0354 決定3/4:
// **テスト fixture が仕込む「前取引日の基準資金」は、どの瞬間に実行しても前取引日でなければならない。**
//
// `EfCapitalBaselineStore.GetCurrent()` は `TradingDay < today`（**米国東部時間の暦日**）の行しか
// 判定に使わない。よって `RiskWorkerWebApplicationFactory` のシードが当日へ落ちると基準資金が `null` になり、
// 新規建てを前提にするテストが `CapitalBaselineUnavailable` で落ちる（fail-closed）。
//
// 🔴 ここは**壁時計に依存しない**。`DateTimeOffset.UtcNow` を読まず、対象の期間を自分で刻んで
// タイムゾーン変換だけを行う純粋な計算である（#885 の実時間依存フレークと同じ形にしないため）。
public class CapitalBaselineSeedTests
{
    private static readonly TimeZoneInfo UsEastern = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    // 夏時間の切り替えを 2 回ずつ含む走査区間（1 分刻み ＝ 1,051,200 点）。
    private static readonly DateTimeOffset SweepFrom = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SweepTo = new(2028, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>さかのぼり日数 <paramref name="daysAgo"/> で「当日へ落ちる」瞬間を列挙する。</summary>
    private static List<DateTimeOffset> CollapsingInstants(int daysAgo)
    {
        var collapsed = new List<DateTimeOffset>();
        for (var now = SweepFrom; now < SweepTo; now = now.AddMinutes(1))
        {
            var today = TradingDay.Of(now, Market.UnitedStates);
            var seeded = TradingDay.Of(now.AddDays(-daysAgo), Market.UnitedStates);
            if (seeded >= today)
                collapsed.Add(now);
        }

        return collapsed;
    }

    // T-10-682（プロパティベース）: 採用しているさかのぼり日数では、走査区間のどの分にも破れが無い。
    // 48 時間前は暦日（最長 25 時間）を必ず 1 日以上またぐため、当日と同じ米国東部暦日には落ちない。
    [Fact]
    public void 前取引日シードは_どの瞬間でも_当日より前の米国東部暦日に落ちる()
    {
        var collapsed = CollapsingInstants(RiskWorkerWebApplicationFactory.CapitalBaselineSeedDaysAgo);

        collapsed.Should().BeEmpty(
            "シードが当日の取引日に落ちると EfCapitalBaselineStore.GetCurrent() が null を返し、"
            + "新規建てが CapitalBaselineUnavailable で止まる");
    }

    // T-10-682（続き）: 前取引日であるだけでは足りない。鮮度上限（既定 4 日）を超えると
    // GetCurrent() は同じく null を返すため、さかのぼりは上限の内側でなければならない。
    [Fact]
    public void 前取引日シードは_基準資金の鮮度上限の内側に収まる()
    {
        var age = TimeSpan.FromDays(RiskWorkerWebApplicationFactory.CapitalBaselineSeedDaysAgo);

        age.Should().BeLessThan(new CapitalBaselineOptions().MaxAge,
            "鮮度上限を超えた観測は「照会できていない」と同じに扱われる（IADR-0354 決定4）");
    }

    // 🔴 T-10-683（境界値・是正の根拠を失わないための退行記録）: 24 時間前では破れる瞬間が実在する。
    // 米国東部で夏時間が終わる日は 25 時間あり、その日の最後の 1 時間（ET 23:00〜23:59）だけ
    // 24 時間前が同じ暦日へ落ちる。年 60 分・2 年で 120 分（#905 の実測と一致する）。
    [Fact]
    public void 二十四時間前では_夏時間終了日の最後の一時間だけ_当日へ落ちる()
    {
        var collapsed = CollapsingInstants(1);

        collapsed.Should().HaveCount(120, "夏時間が終わる日ごとに 60 分（2026 年・2027 年の 2 回）");

        var local = collapsed
            .Select(i => TimeZoneInfo.ConvertTime(i, UsEastern))
            .ToList();

        local.Should().OnlyContain(t => t.Hour == 23, "破れるのは 25 時間ある日の最後の 1 時間だけ");
        local.Select(t => DateOnly.FromDateTime(t.DateTime)).Distinct().Should()
            .BeEquivalentTo([new DateOnly(2026, 11, 1), new DateOnly(2027, 11, 7)],
                "米国の夏時間終了は 11 月第 1 日曜");
        collapsed.Select(i => i.UtcDateTime.Date).Distinct().Should()
            .BeEquivalentTo([new DateTime(2026, 11, 2), new DateTime(2027, 11, 8)],
                "UTC では 04:00〜04:59 の窓（#905 本文の実測）");
    }
}
