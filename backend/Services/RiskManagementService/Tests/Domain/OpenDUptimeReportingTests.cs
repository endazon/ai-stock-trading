using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-06, FR-20, #569, INDEX 決定34, 06_daytrading-review §4.2, IADR-0271:
// 報告書へ供給する OpenD 稼働率の純関数（境界値・プロパティ・否定形の 3 点セット）。
public class OpenDUptimeReportingTests
{
    // 2026-08-05 は水曜（平日＝2 仮説）。2026-08-08 は土曜（分母 0 の仮説 1 つ）。
    private static readonly DateOnly Wednesday = new(2026, 8, 5);
    private static readonly DateOnly Saturday = new(2026, 8, 8);

    private static Stage1SessionUptime Uptime(
        DateOnly date, BrokerProvider provider, int earlyMinutes, int regularMinutes) =>
        new(date, provider, 0, earlyMinutes, regularMinutes);

    // ---- 稼働率そのもの（Stage1SessionHypotheses.UptimeRatio） ----

    // 🔴 **仮説の最小値である。** 半日取引日の窓（210 分）を満たしていても、通常日の窓（390 分）で
    // 割れば低い。どちらがその日の実際の通常取引時間かを実装は知らないため、低い方を採る。
    [Fact]
    public void 稼働率は2仮説の最小値である()
    {
        // 9:30〜13:00 だけ稼働: 半日窓 210/210 = 1.0、通常窓 210/390 ≒ 0.538。
        var uptime = Uptime(Wednesday, BrokerProvider.MoomooSimulate, 210, 210);

        Stage1SessionHypotheses.UptimeRatio(uptime).Should().Be(210m / 390m);
    }

    [Fact]
    public void 週末は分母0の仮説しか無く稼働率は0になる()
    {
        // 分母 0（市場休場）は稼働率が定義できないため 0（Stage1DayQualification.UptimeRatio の明文）。
        Stage1SessionHypotheses.UptimeRatio(Uptime(Saturday, BrokerProvider.MoomooSimulate, 210, 390))
            .Should().Be(0m);
    }

    // **境界値**: ちょうど 50% は算入する（「50% 以上」）。
    [Theory]
    [InlineData(105, 195, true)]   // 半日窓 105/210 = 0.50 ちょうど・通常窓 195/390 = 0.50 ちょうど
    [InlineData(104, 195, false)]  // 半日窓が 0.495…（50% 未満）
    [InlineData(105, 194, false)]  // 通常窓が 0.497…（50% 未満）
    [InlineData(210, 390, true)]   // 全日稼働
    [InlineData(0, 0, false)]      // 観測なし
    public void 稼働率の境界で算入可否が切り替わる(int early, int regular, bool counted)
    {
        var uptime = Uptime(Wednesday, BrokerProvider.MoomooSimulate, early, regular);

        (Stage1SessionHypotheses.UptimeRatio(uptime) >= Stage1DayQualification.MinimumUptimeRatio)
            .Should().Be(counted);
    }

    // 🔴 **プロパティ**: 報告書は比率だけを受け取って算入可否を導く。したがって
    // 「比率 >= 50%」と権威源の `MeetsUptimeThreshold` は**全入力で一致**しなければならない。
    // 一致しないと、「算入」と描いた日が権威源では算入されていない状態が生じる。
    [Fact]
    public void 比率から導く算入可否は権威源の判定と全入力で一致する()
    {
        var mismatches = new List<string>();

        for (var day = 0; day < 14; day++)
        {
            var date = Wednesday.AddDays(day);
            for (var early = 0; early <= 210; early += 7)
            {
                for (var regular = 0; regular <= 390; regular += 13)
                {
                    var uptime = Uptime(date, BrokerProvider.MoomooSimulate, early, regular);
                    var fromRatio = Stage1SessionHypotheses.UptimeRatio(uptime) >= Stage1DayQualification.MinimumUptimeRatio;
                    var authoritative = Stage1SessionHypotheses.MeetsUptimeThreshold(uptime);

                    if (fromRatio != authoritative)
                        mismatches.Add($"{date} early={early} regular={regular}");
                }
            }
        }

        mismatches.Should().BeEmpty();
    }

    // ---- 母集団（OpenD を経由する発注先だけ） ----

    // 🔴 **否定形**: 内蔵 paper は外部へ一度も発注しない＝OpenD を経由しないため、
    // その稼働は OpenD 稼働率ではない。**0% の日として並べもしない**（現れない）。
    [Fact]
    public void 内蔵paperの観測はOpenD稼働率に現れない()
    {
        var days = OpenDUptimeReporting.Days(
            [Uptime(Wednesday, BrokerProvider.InternalPaper, 210, 390)]);

        days.Should().BeEmpty();
    }

    // **対の肯定形**: OpenD を経由する発注先の観測は現れる（不在の表明だけでは母集団が空でも緑になる）。
    [Theory]
    [InlineData(BrokerProvider.MoomooSimulate)]
    [InlineData(BrokerProvider.MoomooReal)]
    public void OpenDを経由する発注先の観測は稼働率に現れる(BrokerProvider provider)
    {
        var days = OpenDUptimeReporting.Days([Uptime(Wednesday, provider, 210, 390)]);

        days.Should().ContainSingle();
        days[0].SessionDateEasternTime.Should().Be(Wednesday);
        days[0].UptimeRatio.Should().Be(1m);
    }

    [Fact]
    public void 同じ取引日に複数の発注先があれば最大値を採る()
    {
        var days = OpenDUptimeReporting.Days(
        [
            Uptime(Wednesday, BrokerProvider.MoomooSimulate, 105, 195),  // 0.50
            Uptime(Wednesday, BrokerProvider.MoomooReal, 210, 390),      // 1.00
        ]);

        days.Should().ContainSingle();
        days[0].UptimeRatio.Should().Be(1m);
    }

    [Fact]
    public void 取引日の昇順で返す()
    {
        var days = OpenDUptimeReporting.Days(
        [
            Uptime(Wednesday.AddDays(2), BrokerProvider.MoomooSimulate, 210, 390),
            Uptime(Wednesday, BrokerProvider.MoomooSimulate, 210, 390),
            Uptime(Wednesday.AddDays(1), BrokerProvider.MoomooSimulate, 210, 390),
        ]);

        days.Select(d => d.SessionDateEasternTime)
            .Should().ContainInOrder(Wednesday, Wednesday.AddDays(1), Wednesday.AddDays(2));
    }

    // 許可制であること（拒否リストではない）を列挙全体で固定する。
    [Theory]
    [InlineData(BrokerProvider.InternalPaper, false)]
    [InlineData(BrokerProvider.MoomooReal, true)]
    [InlineData(BrokerProvider.MoomooSimulate, true)]
    public void OpenDを経由する発注先は許可制である(BrokerProvider provider, bool expected)
    {
        OpenDUptimeReporting.IsOpenDBacked(provider).Should().Be(expected);
    }
}
