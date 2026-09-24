using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, #935, IADR-0394 決定1/3/6: 決済の承認（由来・承認時刻・約定時刻）から「当日の損切り」を 3 値で射影する純関数。
// どの決済を損切りと数えるか（裁定が仕様書での明記を求めた点）と、取引日の区切り（市場の現地取引日）を固定する。
// 時刻はすべて固定値（壁時計を読まない）。
public class StopOutProjectionTests
{
    // 2026-09-23 の実測（稼働 PoC）。S1 の発動 13:43:33Z ＝ 22:43:33 JST ＝ 09:43:33 EDT。
    private static readonly DateTimeOffset StopOutAt = new(2026, 9, 23, 13, 43, 33, TimeSpan.Zero);

    // 同じ AAPL の新規買いが出た時刻（発動の 3 分後）。
    private static readonly DateTimeOffset BuyAttemptAt = new(2026, 9, 23, 13, 46, 45, TimeSpan.Zero);

    private static LedgerCloseApproval Close(
        ApprovalSource? source,
        DateTimeOffset approvedAt,
        TradeSide side = TradeSide.Sell,
        Market market = Market.UnitedStates,
        DateTimeOffset[]? fills = null) =>
        new(Guid.NewGuid(), "AAPL", market, side, source, approvedAt, fills ?? []);

    private static StopOutReentrySupply Project(DateTimeOffset now, params LedgerCloseApproval[] closes) =>
        StopOutProjection.Project(closes, Market.UnitedStates, now);

    // T-10-770: 売りの決済（ロングの損切り）はロング側、買いの決済（ショートの損切り）はショート側に立つ。
    [Fact]
    public void 決済の方向で損切りした建玉の方向が決まる()
    {
        Project(BuyAttemptAt, Close(ApprovalSource.SoftwareStopS1, StopOutAt, TradeSide.Sell))
            .Should().Be(new StopOutReentrySupply(StopOutStatus.StoppedOut, StopOutStatus.None));
        Project(BuyAttemptAt, Close(ApprovalSource.SoftwareStopS1, StopOutAt, TradeSide.Buy))
            .Should().Be(new StopOutReentrySupply(StopOutStatus.None, StopOutStatus.StoppedOut));
    }

    // T-10-771: S1 は**発動（承認）した時点**で数える。約定を待たない（9/23 は発動の 3 分後に買い直した）。
    [Fact]
    public void S1は約定を待たずに発動した取引日に数える()
    {
        Project(BuyAttemptAt, Close(ApprovalSource.SoftwareStopS1, StopOutAt))
            .LongSide.Should().Be(StopOutStatus.StoppedOut);
    }

    // T-10-771: S0 は**約定が当日**のときだけ数える。武装（承認）しただけ・約定が前日は数えない。
    [Fact]
    public void S0は武装ではなく当日の約定で数える()
    {
        var armedDaysAgo = StopOutAt.AddDays(-3);

        // 武装だけ（当日の武装でも約定が無ければ損切りは成立していない）。
        Project(BuyAttemptAt, Close(ApprovalSource.ProtectiveStopS0, StopOutAt))
            .LongSide.Should().Be(StopOutStatus.None);
        // 何日も前に武装し、当日に約定した＝当日の損切り。
        Project(BuyAttemptAt, Close(ApprovalSource.ProtectiveStopS0, armedDaysAgo, fills: [StopOutAt]))
            .LongSide.Should().Be(StopOutStatus.StoppedOut);
        // 約定が前日（ET 9/22）なら当日の損切りではない。
        Project(BuyAttemptAt, Close(ApprovalSource.ProtectiveStopS0, armedDaysAgo, fills: [StopOutAt.AddDays(-1)]))
            .LongSide.Should().Be(StopOutStatus.None);
    }

    // T-10-772: 損切りと数えない決済（保護喪失の成行手仕舞い・OrderApproved 経由の決済＝判断由来・owner の手仕舞い・
    // 維持率割れの自動縮小）は、当日に承認・約定していても「無し」である。
    [Theory]
    [InlineData(ApprovalSource.ProtectionLostClose)]
    [InlineData(ApprovalSource.OrderApproved)]
    public void 損切り以外の決済は数えない(ApprovalSource source)
    {
        Project(BuyAttemptAt, Close(source, StopOutAt, fills: [StopOutAt.AddSeconds(2)]))
            .Should().Be(StopOutReentrySupply.NoneToday);
    }

    // T-10-773: 🔴 由来が記録されていない当日の決済は「不明」（無しとして扱わない）。
    [Fact]
    public void 由来の無い当日の決済は不明になる()
    {
        Project(BuyAttemptAt, Close(null, StopOutAt))
            .LongSide.Should().Be(StopOutStatus.Unknown);
        // 約定だけが当日でも不明（S0 の旧い承認行の形）。
        Project(BuyAttemptAt, Close(null, StopOutAt.AddDays(-3), fills: [StopOutAt]))
            .LongSide.Should().Be(StopOutStatus.Unknown);
        // 前日の由来不明の決済は当日の判定に関係しない。
        Project(BuyAttemptAt, Close(null, StopOutAt.AddDays(-1)))
            .LongSide.Should().Be(StopOutStatus.None);
    }

    // T-10-773: 同じ方向に損切りと不明が並んだら、より具体的な「損切り済み」を採る。
    [Fact]
    public void 損切りと不明が並ぶときは損切り済みを採る()
    {
        Project(BuyAttemptAt, Close(null, StopOutAt), Close(ApprovalSource.SoftwareStopS1, StopOutAt))
            .LongSide.Should().Be(StopOutStatus.StoppedOut);
    }

    // T-10-775: **9/23 の時系列**。区切りは米国東部の暦日であり、JST の日付が変わっても解けない。
    [Theory]
    // 発動の 3 分後（実測の買い）: 同じ ET 9/23 → 止める。
    [InlineData("2026-09-23T13:46:45Z", StopOutStatus.StoppedOut)]
    // 5 分後の 2 本目（713 株）も同じ。
    [InlineData("2026-09-23T13:51:45Z", StopOutStatus.StoppedOut)]
    // JST は 9/24 00:30 に変わったが、ET はまだ 9/23 11:30 → 止める（JST で区切ると #249 と同じ誤りになる）。
    [InlineData("2026-09-23T15:30:00Z", StopOutStatus.StoppedOut)]
    // ET 9/23 23:59:59（EDT）→ まだ止める。
    [InlineData("2026-09-24T03:59:59Z", StopOutStatus.StoppedOut)]
    // ET 9/24 00:00（EDT）→ 翌取引日なので通す。
    [InlineData("2026-09-24T04:00:00Z", StopOutStatus.None)]
    // 翌日の寄り付き後（ET 9/24 09:43）→ 通す。
    [InlineData("2026-09-24T13:43:33Z", StopOutStatus.None)]
    public void 九月二十三日の時系列は米国東部の取引日で区切る(string now, StopOutStatus expected)
    {
        Project(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture),
                Close(ApprovalSource.SoftwareStopS1, StopOutAt))
            .LongSide.Should().Be(expected);
    }

    // T-10-776: 夏時間の境界。冬（EST・UTC−5）は ET の日付が UTC 05:00 に変わる（夏の 04:00 ではない）。
    // 固定オフセット（−4）で換算すると、04:30Z を翌日と誤って通してしまう。
    [Theory]
    // 引け（EST 16:00 ＝ 21:00Z）に S0 が約定した日の夜 23:30 EST ＝ 翌 04:30Z → 同じ ET 1/15 → 止める。
    [InlineData("2026-01-16T04:30:00Z", StopOutStatus.StoppedOut)]
    // 翌 00:30 EST ＝ 05:30Z → ET 1/16 → 通す。
    [InlineData("2026-01-16T05:30:00Z", StopOutStatus.None)]
    public void 冬時間では米国東部の日付がUTC五時に変わる(string now, StopOutStatus expected)
    {
        var filledAtClose = new DateTimeOffset(2026, 1, 15, 21, 0, 0, TimeSpan.Zero);

        Project(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture),
                Close(ApprovalSource.ProtectiveStopS0, filledAtClose.AddDays(-2), fills: [filledAtClose]))
            .LongSide.Should().Be(expected);
    }

    // T-10-776: 夏時間の開始日（2026-03-08）を跨ぐ。開始前の金曜 3/6 の損切りは、開始後の月曜 3/9 には解けている
    // （暦日の比較であり、時差の変化で日付を取り違えない）。同じ月曜の損切りは同じ日のうち止める。
    [Fact]
    public void 夏時間の開始を跨いでも取引日で区切る()
    {
        var fridayStopOut = new DateTimeOffset(2026, 3, 6, 20, 30, 0, TimeSpan.Zero); // EST 15:30
        var mondayMorning = new DateTimeOffset(2026, 3, 9, 13, 45, 0, TimeSpan.Zero); // EDT 09:45

        Project(mondayMorning, Close(ApprovalSource.SoftwareStopS1, fridayStopOut))
            .LongSide.Should().Be(StopOutStatus.None);
        Project(mondayMorning.AddHours(1), Close(ApprovalSource.SoftwareStopS1, mondayMorning))
            .LongSide.Should().Be(StopOutStatus.StoppedOut);
    }

    // 別市場の同一コードは混ぜない（禁止銘柄・差金決済防止と同じ規律。#26）。
    [Fact]
    public void 別市場の決済は数えない()
    {
        Project(BuyAttemptAt, Close(ApprovalSource.SoftwareStopS1, StopOutAt, market: Market.Japan))
            .Should().Be(StopOutReentrySupply.NoneToday);
    }
}
