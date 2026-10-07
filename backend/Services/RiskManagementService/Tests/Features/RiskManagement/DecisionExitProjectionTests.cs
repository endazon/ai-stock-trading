using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, #1176, IADR-0495 決定3: 決済の承認（由来・承認時刻・約定時刻）から「当日の判断由来の決済（利確・判断の手仕舞い）」を方向ごとに射影する
// 純関数（StopOutProjectionTests と同じ形）。数える由来・当日の区切り（市場の現地取引日）・方向を固定する。時刻はすべて固定値。
public class DecisionExitProjectionTests
{
    // issue の実例: AMZN を 2026-10-07 02:34:50 JST（＝10-06 17:34:50Z ＝ 13:34:50 EDT）に利確で全量売却し、
    // 5 分後の 02:39:56 JST（17:39:56Z）に同じ AMZN を新規に買い直した。
    private static readonly DateTimeOffset ExitAt = new(2026, 10, 6, 17, 34, 50, TimeSpan.Zero);
    private static readonly DateTimeOffset RebuyAt = new(2026, 10, 6, 17, 39, 56, TimeSpan.Zero);

    private static LedgerCloseApproval Close(
        ApprovalSource? source,
        DateTimeOffset approvedAt,
        TradeSide side = TradeSide.Sell,
        Market market = Market.UnitedStates,
        DateTimeOffset[]? fills = null) =>
        new(Guid.NewGuid(), "AMZN", market, side, source, approvedAt, fills ?? []);

    private static DecisionExitReentrySupply Project(DateTimeOffset now, params LedgerCloseApproval[] closes) =>
        DecisionExitProjection.Project(closes, Market.UnitedStates, now);

    // T-10-2315: 売りの決済（ロングの利確）はロング側、買いの決済（ショートの手仕舞い）はショート側に立つ。AMZN の 5 分後の買い直しは止まる。
    [Fact]
    public void T_10_2315_決済の方向で判断で手仕舞った建玉の方向が決まる()
    {
        Project(RebuyAt, Close(ApprovalSource.TradeDecision, ExitAt, TradeSide.Sell, fills: [ExitAt.AddSeconds(1)]))
            .Should().Be(new DecisionExitReentrySupply(LongSide: true, ShortSide: false));
        Project(RebuyAt, Close(ApprovalSource.TradeDecision, ExitAt, TradeSide.Buy))
            .Should().Be(new DecisionExitReentrySupply(LongSide: false, ShortSide: true));
        Project(RebuyAt).Should().Be(DecisionExitReentrySupply.NoneToday);
    }

    // T-10-2315: 翌取引日（米国東部の翌暦日）には解ける。別市場の同一コードは数えない。
    [Fact]
    public void T_10_2315_翌取引日と別市場は数えない()
    {
        // 10-06 の ET 取引日に決済 → 10-07 09:31 EDT（13:31Z）の買いは止めない。
        Project(new DateTimeOffset(2026, 10, 7, 13, 31, 0, TimeSpan.Zero), Close(ApprovalSource.TradeDecision, ExitAt))
            .Should().Be(DecisionExitReentrySupply.NoneToday);
        Project(RebuyAt, Close(ApprovalSource.TradeDecision, ExitAt, market: Market.Japan))
            .Should().Be(DecisionExitReentrySupply.NoneToday);
    }

    // T-10-2316: 区切りは米国東部の暦日。JST の日付が変わっても（10-07 00:00 JST ＝ 10-06 15:00Z）同じ ET 取引日なら止める。
    // 夏時間（EDT・UTC−4）では ET の日付は 04:00Z に変わる。
    [Theory]
    [InlineData("2026-10-06T15:00:00Z", true)]  // 10-07 00:00 JST・10-06 11:00 EDT（同じ ET 日）
    [InlineData("2026-10-07T03:59:59Z", true)]  // 10-06 23:59:59 EDT
    [InlineData("2026-10-07T04:00:00Z", false)] // 10-07 00:00 EDT（翌 ET 日）
    public void T_10_2316_夏時間の米国東部の取引日で区切る(string now, bool blocked) =>
        Project(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture), Close(ApprovalSource.TradeDecision, ExitAt))
            .LongSide.Should().Be(blocked);

    // T-10-2316: 冬時間（EST・UTC−5）では ET の日付は 05:00Z に変わる（2026-11-01 に夏時間が終わる）。
    [Theory]
    [InlineData("2026-11-03T04:30:00Z", true)]  // 11-02 23:30 EST（同じ ET 日）
    [InlineData("2026-11-03T05:00:00Z", false)] // 11-03 00:00 EST
    public void T_10_2316_冬時間では米国東部の日付がUTC五時に変わる(string now, bool blocked)
    {
        var exit = new DateTimeOffset(2026, 11, 2, 15, 0, 0, TimeSpan.Zero); // 11-02 10:00 EST
        Project(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture), Close(ApprovalSource.TradeDecision, exit))
            .LongSide.Should().Be(blocked);
    }

    // T-10-2316: 夏時間の終わり（2026-11-01 02:00 EDT → 01:00 EST）を跨いだ同じ ET 日の中では止め続ける。
    [Fact]
    public void T_10_2316_夏時間の終わりを跨いでも同じ取引日なら止める()
    {
        var exit = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);   // 11-01 01:30 EDT（切り替え前）
        var later = new DateTimeOffset(2026, 11, 2, 4, 30, 0, TimeSpan.Zero);  // 11-01 23:30 EST（同じ ET 日）
        var nextDay = new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero); // 11-02 00:00 EST

        Project(later, Close(ApprovalSource.TradeDecision, exit)).LongSide.Should().BeTrue();
        Project(nextDay, Close(ApprovalSource.TradeDecision, exit)).LongSide.Should().BeFalse();
    }

    // T-10-2316: 日本株は JST の暦日で区切る（UTC 15:00 で日付が変わる）。
    [Theory]
    [InlineData("2026-10-07T14:59:59Z", true)]
    [InlineData("2026-10-07T15:00:00Z", false)]
    public void T_10_2316_日本株は日本時間の取引日で区切る(string now, bool blocked)
    {
        var exit = new DateTimeOffset(2026, 10, 7, 1, 0, 0, TimeSpan.Zero); // 10-07 10:00 JST
        var close = new LedgerCloseApproval(Guid.NewGuid(), "7203", Market.Japan, TradeSide.Sell, ApprovalSource.TradeDecision, exit, []);

        DecisionExitProjection.Project([close], Market.Japan, DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture))
            .LongSide.Should().Be(blocked);
    }

    // T-10-2317: 🔴 判断由来の決済だけを数える。損切り（S1 の発動・S0 の約定）・保護喪失・owner の手仕舞いや自動縮小（OrderApproved）・
    // 由来の無い行は、当日に承認・約定していても本統制では数えない（損切りは StoppedOutSameDay、由来なしは StopOutStatusUnknown が止める）。
    [Theory]
    [InlineData(ApprovalSource.SoftwareStopS1)]
    [InlineData(ApprovalSource.ProtectiveStopS0)]
    [InlineData(ApprovalSource.ProtectionLostClose)]
    [InlineData(ApprovalSource.OrderApproved)]
    [InlineData(null)]
    public void T_10_2317_判断由来でない決済は数えない(ApprovalSource? source)
    {
        Project(RebuyAt, Close(source, ExitAt, fills: [ExitAt.AddSeconds(2)]))
            .Should().Be(DecisionExitReentrySupply.NoneToday);
    }

    // T-10-2317: 損切りの射影は判断由来の決済を数えない（理由を混ぜない）。
    [Fact]
    public void T_10_2317_損切りの射影は判断由来の決済を数えない() =>
        StopOutProjection.Project([Close(ApprovalSource.TradeDecision, ExitAt, fills: [ExitAt])], Market.UnitedStates, RebuyAt)
            .Should().Be(StopOutReentrySupply.NoneToday);

    // T-10-2318: 承認だけ（約定が台帳へ届く前・ブローカーの拒否で約定しなかった）でも数える。部分約定でも同じ。
    // 承認が前日でも約定が当日なら数える。承認・約定とも前日なら数えない。
    [Fact]
    public void T_10_2318_承認だけでも部分約定でも数え前日の承認は当日の約定で数える()
    {
        Project(RebuyAt, Close(ApprovalSource.TradeDecision, ExitAt)).LongSide.Should().BeTrue("約定を待たない");
        Project(RebuyAt, Close(ApprovalSource.TradeDecision, ExitAt, fills: [ExitAt.AddSeconds(1), ExitAt.AddSeconds(3)]))
            .LongSide.Should().BeTrue("部分約定（複数の約定行）");

        var yesterday = ExitAt.AddDays(-1);
        Project(RebuyAt, Close(ApprovalSource.TradeDecision, yesterday, fills: [ExitAt])).LongSide.Should().BeTrue();
        Project(RebuyAt, Close(ApprovalSource.TradeDecision, yesterday, fills: [yesterday])).LongSide.Should().BeFalse();
    }

    // T-10-2411: 🔴 #1209, IADR-0507: 本番の射影（時刻）と共有の述語（取引日）は同値 —— Stage 0 の再生は後者を判断日・約定日で通す。
    // 再生の時間軸（判断日 D に決済を決め、D+1 の始値で約定）を本番の時刻に置いた 3 つの場面で、両方向の答えが一致する:
    //   P1: D の決済が D+1 に約定・D+1 に同じ方向 → 止める／P2: D+2 に同じ方向 → 止めない／P3: 決済が約定しない・D+1 → 止めない。
    [Theory]
    [InlineData("P1", true)]
    [InlineData("P2", false)]
    [InlineData("P3", false)]
    public void T_10_2411_本番の射影と共有の述語は再生の時間軸で同じ答えを返す(string probe, bool blocked)
    {
        var decidedOn = new DateOnly(2026, 10, 5);                                     // 月曜（ET）
        var approvedAt = new DateTimeOffset(2026, 10, 5, 19, 0, 0, TimeSpan.Zero);      // 10-05 15:00 EDT
        var filledAt = new DateTimeOffset(2026, 10, 6, 13, 30, 0, TimeSpan.Zero);       // 10-06 09:30 EDT（翌取引日の始値）
        var (now, fills, filledDays) = probe switch
        {
            "P1" => (new DateTimeOffset(2026, 10, 6, 19, 0, 0, TimeSpan.Zero), new[] { filledAt }, new[] { decidedOn.AddDays(1) }),
            "P2" => (new DateTimeOffset(2026, 10, 7, 19, 0, 0, TimeSpan.Zero), new[] { filledAt }, new[] { decidedOn.AddDays(1) }),
            _ => (new DateTimeOffset(2026, 10, 6, 19, 0, 0, TimeSpan.Zero), Array.Empty<DateTimeOffset>(), Array.Empty<DateOnly>()),
        };
        var today = DateOnly.FromDateTime(now.AddHours(-4).DateTime);

        var production = DecisionExitProjection.Project(
            [Close(ApprovalSource.TradeDecision, approvedAt, TradeSide.Sell, fills: fills)], Market.UnitedStates, now);
        var shared = DecisionExitReentry.Project([new DecisionExitOnTradingDays(TradeSide.Sell, decidedOn, filledDays)], today);

        production.Should().Be(new DecisionExitReentrySupply(shared.LongSide, shared.ShortSide));
        production.ForEntry(TradeSide.Buy).Should().Be(blocked);
        DecisionExitReentry.BlocksEntry(shared.LongSide, shared.ShortSide, TradeSide.Buy).Should().Be(blocked);
        production.ForEntry(TradeSide.Sell).Should().BeFalse("反対方向は止めない");
    }
}
