using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using TradeDecisionService.Features.TradeDecision;
using Xunit;
using static TradeDecisionService.Tests.DailyBarsTestData;

namespace TradeDecisionService.Tests;

// FR-04, FR-02, ADR-0048 決定 2, ADR-0003, #1118, IADR-0467 決定 4: 前営業日の出来高と 20 日平均比の計算（純関数）。
// 🔴 20 日平均は前営業日を含む直近 20 本の単純平均。値が得られない項目は null（不明）で、0 や「少ない」にしない。
public class DailyVolumeContextTests
{
    // ---- T-10-1827: 20 日平均比は前営業日を含む直近 20 本の単純平均に対する比（丸めない） ----
    [Fact]
    public void 比は前営業日を含む直近20本の単純平均に対する比()
    {
        // 古い 5 本は窓の外（大きな値でも平均に入らない）。窓 = 19 本の 1,000 と前営業日の 2,900 → 平均 (19,000 + 2,900) / 20 = 1,095。
        var bars = Bars(Monday, Then(Then(Repeat(1_000_000, 5), Repeat(1_000, 19)), 2_900));

        var v = DailyVolumeContext.From(Confirmed(bars));

        v.PreviousDay.Should().Be(Monday);
        v.PreviousDayVolume.Should().Be(2_900);
        v.Average20.Should().Be(1_095m);
        v.RatioToAverage20.Should().Be(2_900m / 1_095m, "丸めない（表示で丸める）");
    }

    [Fact]
    public void ちょうど20本でも比を出し_平均と同じなら1()
    {
        var v = DailyVolumeContext.From(Confirmed(Bars(Monday, Repeat(5_000, 20))));

        v.Average20.Should().Be(5_000m);
        v.RatioToAverage20.Should().Be(1m);
    }

    // ---- T-10-1828: 足りない・欠け・0 以下 ----
    [Fact]
    public void 確定足が19本なら前営業日の出来高だけ出し比は不明()
    {
        var v = DailyVolumeContext.From(Confirmed(Bars(Monday, Repeat(5_000, 19))));

        v.PreviousDayVolume.Should().Be(5_000);
        v.Average20.Should().BeNull();
        v.RatioToAverage20.Should().BeNull("20 本に満たない平均を 20 日平均と呼ばない");
    }

    // 🔴 否定形: 前営業日の足が無い（最後の足が前営業日より古い）なら、古い足を「前日」として出さない。
    [Fact]
    public void 最後の足が前営業日でなければ未提供_否定形()
    {
        var stale = Bars(new DateOnly(2026, 9, 25), Repeat(5_000, 25));

        DailyVolumeContext.From(Confirmed(stale)).Should().Be(DailyVolumeContext.Unavailable);
    }

    [Fact]
    public void 取得できない_空_前営業日の出来高が0以下なら未提供_否定形()
    {
        DailyVolumeContext.From(null).Should().Be(DailyVolumeContext.Unavailable);
        DailyVolumeContext.From(Confirmed([])).Should().Be(DailyVolumeContext.Unavailable);
        DailyVolumeContext.From(Confirmed(Bars(Monday, Then(Repeat(5_000, 20), 0)))).Should().Be(DailyVolumeContext.Unavailable);
    }

    [Fact]
    public void 平均の窓に0以下の足があれば比は不明()
    {
        var v = DailyVolumeContext.From(Confirmed(Bars(Monday, Then(Then(Repeat(5_000, 10), 0), Repeat(5_000, 9)))));

        v.PreviousDayVolume.Should().Be(5_000);
        v.RatioToAverage20.Should().BeNull("0 の日を平均へ入れると比が過大に見える");
    }

    // ---- T-10-1829: 前復権で揃った足（分割の前も分割比で調整済み）では、分割をまたいでも比は歪まない ----
    // NVDA 10:1（2024-06-10）の実測: 前復権の出来高は分割前も 10 倍へ調整される。前復権の足で数えれば比は 1、
    // 未調整（無復権）の分割前の足を半分混ぜると平均が縮み、比が約 1.8 倍に跳ねる（LLM は「出来高の急増」と読み違える）。
    [Fact]
    public void 前復権で揃った足では分割をまたいでも比は歪まない()
    {
        var forward = Bars(Monday, Repeat(4_000_000, 20)); // 分割前 10 本も 400,000 × 10 に調整済み
        var mixed = Bars(Monday, Then(Repeat(400_000, 10), Repeat(4_000_000, 10))); // 分割前の未調整の足（対照）

        DailyVolumeContext.From(Confirmed(forward)).RatioToAverage20.Should().Be(1m);
        DailyVolumeContext.From(Confirmed(mixed)).RatioToAverage20.Should().BeGreaterThan(1.8m,
            "対照: 基準の違う足を混ぜると比が歪む（だから取得をまたいで足し継がない）");
    }

    // ---- T-10-1830（前営業日の計算）: 週末・規則計算の休場日を飛ばす ----
    [Theory]
    [InlineData("2026-09-29", "2026-09-28")] // 火 → 月
    [InlineData("2026-09-28", "2026-09-25")] // 月 → 金
    [InlineData("2026-09-08", "2026-09-04")] // 労働者の日（9/7 月）の翌日 → 金
    public void 前営業日は週末と休場日を飛ばす(string date, string expected)
    {
        MarketTradingDays.PreviousTradingDay(Market.UnitedStates, DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture))
            .Should().Be(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void 取引日は米国東部の日付で決まる()
    {
        // 2026-09-30 03:30 UTC ＝ 米国東部 9/29 23:30（まだ 9/29）。04:30 UTC ＝ 9/30 00:30。
        MarketTradingDays.TradingDateOf(Market.UnitedStates, new DateTimeOffset(2026, 9, 30, 3, 30, 0, TimeSpan.Zero))
            .Should().Be(new DateOnly(2026, 9, 29));
        MarketTradingDays.TradingDateOf(Market.UnitedStates, new DateTimeOffset(2026, 9, 30, 4, 30, 0, TimeSpan.Zero))
            .Should().Be(new DateOnly(2026, 9, 30));
    }

    // ---- T-10-1842: 取引日は冬時間（EST・UTC-5）と夏時間の切り替えの日でも米国東部の日付（［2026-10-01 追記］監査 🟡-1） ----
    // 固定の -4 時間（夏時間）で換算すると、冬時間の 04:00〜05:00 UTC の取引日が 1 日進む（前日の取得を翌日に使う・
    // 当日の未確定足を前日として読む側へずれる）。固定の -5 時間では夏時間の同じ時間帯が 1 日戻る。どちらの変異も赤にする。
    [Theory]
    [InlineData("2026-11-03T04:30:00Z", "2026-11-02")] // EST: 11/02 23:30（固定 -4 なら 11/03 00:30）
    [InlineData("2026-11-03T05:30:00Z", "2026-11-03")] // EST: 11/03 00:30
    [InlineData("2027-01-15T04:59:00Z", "2027-01-14")] // 真冬の EST: 01/14 23:59
    [InlineData("2026-11-01T04:30:00Z", "2026-11-01")] // 夏時間の終わりの日（06:00 UTC に EST へ）の直前は EDT: 11/01 00:30（固定 -5 なら 10/31）
    [InlineData("2026-11-02T04:30:00Z", "2026-11-01")] // 切り替えの後は EST: 11/01 23:30（固定 -4 なら 11/02）
    [InlineData("2027-03-14T04:30:00Z", "2027-03-13")] // 夏時間の始まりの日（07:00 UTC に EDT へ）の前は EST: 03/13 23:30（固定 -4 なら 03/14）
    [InlineData("2027-03-15T04:30:00Z", "2027-03-15")] // 切り替えの後は EDT: 03/15 00:30（固定 -5 なら 03/14）
    public void 取引日は冬時間と夏時間の切り替えの日でも米国東部の日付(string instantUtc, string expected)
    {
        var instant = DateTimeOffset.Parse(instantUtc, System.Globalization.CultureInfo.InvariantCulture);

        MarketTradingDays.TradingDateOf(Market.UnitedStates, instant)
            .Should().Be(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }
}
