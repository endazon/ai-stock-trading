using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Domain.Tests;

// FR-20, #385, 06_daytrading-review §4.2, IADR-0150:
// 稼働の観測から「その日の稼働分数」を積み、**カレンダーを持たないまま**算入可否を判定する規則を固定する。
//
// 3 点セット: 境界値（両仮説をちょうど満たす 105 分 / 195 分・9:30 と 16:00 の窓境界）／
// **構造テスト**（3 年ぶんの全日付で結果が曜日だけで決まる＝カレンダーが内蔵されていないことの証明）／
// **否定形**（落とした区間・逆行・時間外・上限超え・半日仮説を満たさない午後稼働・内蔵 paper）。
public class Stage1SessionUptimeTests
{
    private const int Open = Stage1SessionHypotheses.RegularSessionOpenMinuteOfDayEasternTime;       // 570（9:30 ET）
    private const int EarlyClose = Stage1SessionHypotheses.EarlyCloseMinuteOfDayEasternTime;         // 780（13:00 ET）
    private const int RegularClose = Stage1SessionHypotheses.RegularCloseMinuteOfDayEasternTime;     // 960（16:00 ET）

    private static readonly DateOnly Weekday = new(2026, 8, 3);   // 月曜
    private static readonly DateOnly Weekend = new(2026, 8, 1);   // 土曜

    private static Stage1SessionUptime Empty(
        DateOnly? date = null, BrokerProvider provider = BrokerProvider.MoomooSimulate) =>
        new(date ?? Weekday, provider);

    /// <summary>[from, to] を <paramref name="step"/> 分刻みの probe で満たす（実際の巡回と同じ積み方）。</summary>
    private static Stage1SessionUptime Probe(Stage1SessionUptime uptime, int from, int to, int step = 30)
    {
        for (var minute = from; minute <= to; minute += step)
        {
            uptime = uptime.Credit(minute, step);
        }

        return uptime;
    }

    // ------------------------------------------------------------------
    // 1. 稼働分数の積み方（IADR-0150 決定2）
    // ------------------------------------------------------------------

    // 正: 9:30〜16:00 を 30 分刻みで観測すれば、両方の窓が満たされる。
    [Fact]
    public void 場中を通して観測できた日は両方の窓が満たされる()
    {
        var uptime = Probe(Empty(), Open, RegularClose);

        uptime.OperationalMinutesBeforeEarlyClose.Should().Be(EarlyClose - Open);       // 210
        uptime.OperationalMinutesBeforeRegularClose.Should().Be(RegularClose - Open);   // 390
    }

    // **否定形**: 初回の観測は遡らない。どこから稼働していたかを知らないためである。
    [Fact]
    public void 初回の観測は遡って積まない()
    {
        var uptime = Empty().Credit(Open + 30, 30);

        uptime.OperationalMinutesBeforeRegularClose.Should().Be(0);
        uptime.LastObservedMinuteOfDayEasternTime.Should().Be(Open + 30);
    }

    // **否定形（本 issue の核心）**: probe を 1 回でも落とした区間は稼働として積まない。
    // 「前回成功から今回成功までを一律に埋める」実装だと、その間に起きた停止が稼働として計上される（水増し）。
    [Fact]
    public void probeを落とした区間は稼働として積まない()
    {
        // 10:00 に成功 → 10:30 の巡回を落とす → 11:00 に成功（間隔 30 分に対し経過 60 分）。
        var uptime = Empty()
            .Credit(600, 30)   // 10:00（初回・0 分）
            .Credit(630, 30)   // 10:30（30 分）
            .Credit(690, 30);  // 11:30（60 分経過＝落としている）

        uptime.OperationalMinutesBeforeRegularClose.Should().Be(30, "落とした 60 分は積まない");
        uptime.LastObservedMinuteOfDayEasternTime.Should().Be(690, "観測位置そのものは進む");
    }

    // **否定形**: 逆行・同時刻の再送は積まない（冪等）。イベントの再配送で分数が二重に増えない。
    [Fact]
    public void 逆行と再送は積まない()
    {
        var uptime = Empty().Credit(600, 30).Credit(630, 30);
        var after = uptime.Credit(630, 30).Credit(600, 30);

        after.Should().Be(uptime, "同時刻・逆行の観測は状態を変えない");
    }

    // **否定形**: 1 件の観測は上限（30 分）を超えて積めない。
    // 供給側の設定ミス・改変で 1 件が 1 日を埋めることを構造的に防ぐ。
    [Fact]
    public void 一件の観測は上限を超えて積まない()
    {
        var uptime = Empty()
            .Credit(Open, 600)              // 初回（0 分）
            .Credit(RegularClose, 600);     // 390 分ぶんを 1 件で主張する

        uptime.OperationalMinutesBeforeRegularClose.Should().Be(
            0, "上限 30 分でクランプされ、390 分の経過は『落とした区間』になる");
    }

    // **否定形**: 時間外（9:30 前・16:00 後）の稼働は積まない（§4.2「プレ／アフターマーケットは含めない」）。
    [Fact]
    public void 時間外の稼働は積まない()
    {
        var beforeOpen = Probe(Empty(), Open - 240, Open);       // 5:30〜9:30
        var afterClose = Probe(Empty(), RegularClose, RegularClose + 240); // 16:00〜20:00

        beforeOpen.OperationalMinutesBeforeRegularClose.Should().Be(0);
        afterClose.OperationalMinutesBeforeRegularClose.Should().Be(0);
    }

    // ------------------------------------------------------------------
    // 2. 算入の判定（IADR-0150 決定3・両仮説）
    // ------------------------------------------------------------------

    // 正: 場中を通して稼働した平日は算入される。
    [Fact]
    public void 場中を通して稼働した平日は算入される()
    {
        Stage1SessionHypotheses.Qualifies(Probe(Empty(), Open, RegularClose)).Should().BeTrue();
    }

    // **境界値**: 両仮説をちょうど満たす（半日仮説 105 分／通常日仮説 195 分）と算入される。
    // 9:30〜12:45 は半日窓で 195 分（≥105）、通常日窓でも 195 分（＝ちょうど 50%）。
    [Fact]
    public void 両仮説をちょうど満たす稼働は算入される()
    {
        var uptime = Probe(Empty(), Open, Open + 195, step: 15);

        uptime.OperationalMinutesBeforeEarlyClose.Should().Be(195);
        uptime.OperationalMinutesBeforeRegularClose.Should().Be(195);
        Stage1SessionHypotheses.Qualifies(uptime).Should().BeTrue();
    }

    // **境界値の否定形**: 1 分足りない（194 分）と算入されない（通常日仮説が 50% を割る）。
    [Fact]
    public void 通常日仮説に1分足りなければ算入されない()
    {
        var uptime = Probe(Empty(), Open, Open + 194, step: 2);

        uptime.OperationalMinutesBeforeRegularClose.Should().Be(194);
        Stage1SessionHypotheses.Qualifies(uptime).Should().BeFalse();
    }

    // 正（**過剰に厳しくないことの確認**）: 9:30〜13:00 を通して稼働した日は算入される。
    // その日が半日取引日なら 210 ÷ 210 ＝ 100%、通常日でも 210 ÷ 390 ＝ **53.8%** で
    // いずれも閾値 50% 以上である。**どちらの仮説でも算入される日を、実装は落とさない。**
    //
    // 「分母を常に 390 分と仮定すれば半日取引日は必ず除外される」という読みは**算数が誤っている**
    // （53.8% は 50% を割らない）。両仮説方式が落とすのは、あくまで**仮説によって結論が割れる日**だけである。
    [Fact]
    public void 半日ぶんの満稼働はどちらの仮説でも算入される()
    {
        var uptime = Probe(Empty(), Open, EarlyClose);

        uptime.OperationalMinutesBeforeEarlyClose.Should().Be(210, "半日仮説では 100% 稼働している");
        uptime.OperationalMinutesBeforeRegularClose.Should().Be(210, "通常日仮説でも 53.8%（≥ 50%）である");
        Stage1SessionHypotheses.Qualifies(uptime).Should().BeTrue();
    }

    // **否定形（監査が指摘した穴を塞ぐ確認）**: 午後だけ稼働（12:00〜16:00 の 240 分）は算入しない。
    // 分母を常に 390 分と仮定する実装では 240 ÷ 390 ＝ 61.5% で算入されるが、
    // その日が半日取引日なら実際の稼働は 13:00 までの 60 分（60 ÷ 210 ＝ 28.6%）でしかない。
    // 両仮説方式はこの日を落とす。
    [Fact]
    public void 午後だけ稼働した日は算入しない()
    {
        var uptime = Probe(Empty(), 720, RegularClose);   // 12:00〜16:00

        uptime.OperationalMinutesBeforeRegularClose.Should().Be(240, "通常日仮説なら 61.5% で閾値を超える");
        uptime.OperationalMinutesBeforeEarlyClose.Should().Be(60, "半日仮説では 28.6% しかない");
        Stage1SessionHypotheses.Qualifies(uptime).Should().BeFalse("半日取引日だった可能性を否定できない");
    }

    // **否定形**: 週末は満稼働でも算入しない（§4.2 の除外「市場休場」のうち曜日で判る分）。
    [Fact]
    public void 週末は満稼働でも算入しない()
    {
        var uptime = Probe(Empty(Weekend), Open, RegularClose);

        Stage1SessionHypotheses.Qualifies(uptime).Should().BeFalse();
        Stage1SessionHypotheses.MeetsUptimeThreshold(uptime).Should().BeFalse();
    }

    // **否定形**: 内蔵 paper で満稼働しても算入しない（許可制・IADR-0142 決定2）。
    // ただし**除外日として別掲される**（SC-03 の「paper 稼働により N 日を除外」）。
    [Fact]
    public void 内蔵paperの稼働日は算入せず除外日として数える()
    {
        var paper = Probe(Empty(provider: BrokerProvider.InternalPaper), Open, RegularClose);

        Stage1SessionHypotheses.Qualifies(paper).Should().BeFalse();
        Stage1SessionHypotheses.MeetsUptimeThreshold(paper).Should().BeTrue("稼働率の条件そのものは満たしている");
        Stage1SessionHypotheses.CountQualifiedDays([paper]).Should().Be(0);
        Stage1SessionHypotheses.CountExcludedInternalPaperDays([paper]).Should().Be(1);
    }

    // **否定形**: 実弾（MoomooReal）の稼働も算入しない（許可制であり拒否リストではない）。
    [Fact]
    public void 実弾の稼働も算入しない()
    {
        var live = Probe(Empty(provider: BrokerProvider.MoomooReal), Open, RegularClose);

        Stage1SessionHypotheses.Qualifies(live).Should().BeFalse();
        Stage1SessionHypotheses.CountExcludedInternalPaperDays([live]).Should().Be(0, "paper 由来の除外ではない");
    }

    // 同じ日に複数の発注先で稼働しても 1 日は 1 日である（§4.2 は日数を数える）。
    [Fact]
    public void 同じ日に複数の発注先で稼働しても1日として数える()
    {
        var simulate = Probe(Empty(), Open, RegularClose);
        var paper = Probe(Empty(provider: BrokerProvider.InternalPaper), Open, RegularClose);

        Stage1SessionHypotheses.CountQualifiedDays([simulate, paper]).Should().Be(1);
        Stage1SessionHypotheses.CountExcludedInternalPaperDays([simulate, paper])
            .Should().Be(0, "その日は算入されており、paper を理由に除外されていない");
    }

    // ------------------------------------------------------------------
    // 3. 構造テスト: カレンダーを内蔵していないことの証明
    // ------------------------------------------------------------------

    // **カレンダー不在の構造的な証明**（#385 受け入れ基準・IADR-0137 決定1・ADR-0022 決定3）。
    //
    // 「入れていない」という主張ではなく、**入れられていたら落ちるテスト**である。
    // 3 年ぶん（約 1,100 日）のすべての日付に**同一の稼働**を与え、算入可否が **DayOfWeek だけ**で
    // 決まることを確かめる。休場日・半日取引日の表が 1 つでもコードに入っていれば、
    // その日付だけ結果が変わり本テストが落ちる。
    [Fact]
    public void カレンダーを内蔵していない__同じ稼働なら曜日だけで結果が決まる()
    {
        var start = new DateOnly(2026, 1, 1);
        var end = new DateOnly(2028, 12, 31);

        for (var date = start; date <= end; date = date.AddDays(1))
        {
            var uptime = Probe(Empty(date), Open, RegularClose);
            var isWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

            Stage1SessionHypotheses.Qualifies(uptime).Should().Be(
                !isWeekend,
                "算入可否は曜日と稼働分数だけで決まる（休場日・半日取引日の表を実装が持たない）: {0}",
                date);
        }
    }

    // 上のテストが「常に true を返す実装」でも緑にならないことの対照。
    // 米国市場の実在の休場日（2026-01-01 元日・2026-07-03 独立記念日の振替・2026-11-26 感謝祭）と、
    // 実在の半日取引日（2026-11-27 感謝祭翌日・2026-12-24 クリスマスイブ）を名指しで確かめる。
    // **いずれも平日であり、実装はこれらを特別扱いしない**——これが「塞げない穴」（祝日）の所在であり、
    // 握り潰さずにテストで可視化しておく（docs/blocked-tasks.md B-4）。
    [Theory]
    [InlineData(2026, 1, 1)]    // 元日（木曜・市場休場）
    [InlineData(2026, 7, 3)]    // 独立記念日の振替（金曜・市場休場）
    [InlineData(2026, 11, 26)]  // 感謝祭（木曜・市場休場）
    [InlineData(2026, 11, 27)]  // 感謝祭翌日（金曜・**半日取引日**）
    [InlineData(2026, 12, 24)]  // クリスマスイブ（木曜・**半日取引日**）
    public void 祝日と半日取引日を実装は判別しない__既知の限界(int year, int month, int day)
    {
        var date = new DateOnly(year, month, day);
        date.DayOfWeek.Should().NotBe(DayOfWeek.Saturday);
        date.DayOfWeek.Should().NotBe(DayOfWeek.Sunday);

        // 満稼働（＝OpenD は接続され続けていた）なら、実装は平日として算入する。
        // 休場日については**過大計上**であり、判定源が計画側で確定するまで塞げない（IADR-0150 の残余リスク）。
        Stage1SessionHypotheses.Qualifies(Probe(Empty(date), Open, RegularClose)).Should().BeTrue();
    }

    // 仮説の集合そのものを固定する（§4.2 の転記であることの確認）。
    // 9:30 ＋ 210 分 ＝ 13:00／9:30 ＋ 390 分 ＝ 16:00 であり、独立の定数を新設していない。
    [Fact]
    public void 通常取引時間の定数は計画の転記である()
    {
        Open.Should().Be((9 * 60) + 30);
        EarlyClose.Should().Be(Open + Stage1DayQualification.RegularSessionMinutesHalfDay);
        RegularClose.Should().Be(Open + Stage1DayQualification.RegularSessionMinutesFullDay);
        EarlyClose.Should().Be(13 * 60);
        RegularClose.Should().Be(16 * 60);

        Stage1SessionHypotheses.Hypotheses(Empty()).Should().HaveCount(2, "平日は半日・通常日の 2 仮説");
        Stage1SessionHypotheses.Hypotheses(Empty(Weekend)).Should().HaveCount(1, "週末は分母 0 の 1 仮説");
        Stage1SessionHypotheses.Hypotheses(Empty(Weekend))[0].RegularSessionMinutes.Should().Be(0);
    }
}
