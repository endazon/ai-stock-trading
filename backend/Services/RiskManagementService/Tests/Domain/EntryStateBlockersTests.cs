using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// 🔴 FR-10, FR-04, ADR-0003, #1113, IADR-0463 決定 2: 新規建ての可否の口（EntryStateBlockers.Determine）が、**同じ入力で審査
// （RiskEvaluator.Evaluate）の拒否と一致する**こと。述語を 2 か所に置かない（どちらかだけを直すと赤になる）。
//
// 一致の定義:
//   - 健全性（常に）: 口が返す理由は、審査がその新規建てを拒否する理由に必ず含まれる（口が「落ちる」と言えば審査は落とす）。
//   - 完全性（状態が既知のとき）: 審査の拒否理由のうち口の対象（Determinable）に入るものは、口もすべて返す（並びも同じ）。
//   - 不明（StopOutStatusUnknown・GFV 件数の未供給・資金の未供給）は口は返さない（LLM を呼ぶ側へ倒す。裁定 3）。
public class EntryStateBlockersTests
{
    private const int MaxPositions = 3; // TradingDefaults の保有建玉数上限。

    private static readonly RiskManagementSettings Settings = TradingDefaults.CreateSettings();

    private static OrderIntent Entry(TradeSide side) =>
        new("AAPL", Market.UnitedStates, side, ProductType.Cash, BrokerProvider.InternalPaper, 1, 10m, PositionEffect.Open);

    public enum Gfv
    {
        NotCashAccount,
        CashBelowLimit,
        CashAtLimit,
        CashUnknownCount,

        // 🔴 PR #1116 の監査（M6）: 信用口座（照会済み）で GFV 件数が停止基準以上。GFV は現金口座でのみ加わる統制であり、
        // 口座種別の条件を外すとここだけが口に GoodFaithViolationLimitReached を立てる（上の 4 値は Account か件数が null）。
        MarginAtLimit,
    }

    private static PortfolioSnapshot Snapshot(
        int openPositions, bool killSwitch, bool paused, bool dailyLoss, bool drawdown, Gfv gfv, bool capitalKnown) => new()
        {
            Capital = capitalKnown ? 100_000m : null,
            OpenPositionCount = openPositions,
            KillSwitchEngaged = killSwitch,
            TradingPaused = paused,
            // 日次損失 2% ＝ 2,000。到達は 2,000 ちょうど（<=）。
            DailyRealizedPnl = dailyLoss ? -1_500m : 0m,
            UnrealizedPnl = dailyLoss ? -500m : -1_999m,
            DrawdownRatio = drawdown ? Settings.Limits.MaxDrawdownRatio : Settings.Limits.MaxDrawdownRatio - 0.0001m,
            Account = gfv switch
            {
                Gfv.NotCashAccount => null,
                Gfv.MarginAtLimit => new BrokerAccountState(AccountType.Margin, SettledCashInBase: 1_000_000m),
                _ => new BrokerAccountState(AccountType.Cash, SettledCashInBase: 1_000_000m),
            },
            GoodFaithViolations = gfv switch
            {
                Gfv.CashBelowLimit => GoodFaithViolationTally.Observed(AccountTypePolicy.GoodFaithViolationStopThreshold - 1),
                Gfv.CashAtLimit or Gfv.MarginAtLimit =>
                    GoodFaithViolationTally.Observed(AccountTypePolicy.GoodFaithViolationStopThreshold),
                _ => null,
            },
        };

    // T-10-1782: 全組み合わせで口と審査が一致する（建玉数 上限−1/上限/上限+1 × 損切り None/StoppedOut/Unknown（両方向）×
    // 方向 × kill switch × 一時停止 × 日次損失 × DD × GFV（口座種別・件数の既知／不明。信用口座（照会済み）で件数が停止基準以上を
    // 含む＝PR #1116 の監査 M6）× 資金の既知／不明）。
    [Fact]
    public void T_10_1782_口の答えは同じ入力の審査の拒否と一致する()
    {
        var statuses = new[] { StopOutStatus.None, StopOutStatus.StoppedOut, StopOutStatus.Unknown };
        var bools = new[] { false, true };
        var checkedCases = 0;
        var blockedCases = 0;
        var failures = new List<string>();

        var cases =
            from positions in new[] { MaxPositions - 1, MaxPositions, MaxPositions + 1 }
            from longStop in statuses
            from shortStop in statuses
            from longExit in bools
            from shortExit in bools
            from side in new[] { TradeSide.Buy, TradeSide.Sell }
            from killSwitch in bools
            from paused in bools
            from dailyLoss in bools
            from drawdown in bools
            from gfv in Enum.GetValues<Gfv>()
            from capitalKnown in bools
            select (positions, longStop, shortStop, longExit, shortExit, side, killSwitch, paused, dailyLoss, drawdown, gfv, capitalKnown);

        foreach (var (positions, longStop, shortStop, longExit, shortExit, side, killSwitch, paused, dailyLoss, drawdown, gfv, capitalKnown)
            in cases)
        {
            var snapshot = Snapshot(positions, killSwitch, paused, dailyLoss, drawdown, gfv, capitalKnown);
            var stopOuts = new StopOutReentrySupply(longStop, shortStop);
            // T-10-2319, #1176, IADR-0495 決定3: 判断由来の決済（両方向の有無）の次元を足した。
            var exits = new DecisionExitReentrySupply(longExit, shortExit);

            var blockers = EntryStateBlockers.Determine(side, Settings, snapshot, stopOuts, exits, lockedOut: false);
            var screened = RiskEvaluator.Evaluate(Entry(side), Settings, snapshot, stopOuts: stopOuts, decisionExits: exits).Reasons;
            var screenedDeterminable = screened.Where(EntryStateBlockers.Determinable.Contains).ToList();
            var known = gfv != Gfv.CashUnknownCount;

            var label = $"positions={positions} stop={longStop}/{shortStop} exit={longExit}/{shortExit} side={side} kill={killSwitch} pause={paused} " +
                $"loss={dailyLoss} dd={drawdown} gfv={gfv} capital={capitalKnown}: 口=[{string.Join(",", blockers)}] " +
                $"審査=[{string.Join(",", screenedDeterminable)}]";
            if (!blockers.All(screened.Contains))
                failures.Add("健全性 " + label);
            if (known && !blockers.SequenceEqual(screenedDeterminable))
                failures.Add("完全性 " + label);
            if (blockers.Contains(RejectionReason.StopOutStatusUnknown))
                failures.Add("不明を返した " + label);

            checkedCases++;
            if (blockers.Count > 0)
                blockedCases++;
        }

        failures.Should().BeEmpty();
        checkedCases.Should().Be(3 * 3 * 3 * 2 * 2 * 2 * 2 * 2 * 2 * 2 * 5 * 2);
        blockedCases.Should().BeGreaterThan(0).And.BeLessThan(checkedCases, "塞がる組と塞がらない組の両方を試す");
    }

    // T-10-1782: 名指し（上の一致だけでは「どちらも何も返さない」でも緑になるため、個々の理由を確かめる）。
    [Theory]
    [InlineData(MaxPositions - 1, false)]
    [InlineData(MaxPositions, true)]
    [InlineData(MaxPositions + 1, true)]
    public void T_10_1782_保有建玉数が上限に達していれば両方向とも塞がる(int positions, bool blocked)
    {
        var snapshot = Snapshot(positions, false, false, false, false, Gfv.NotCashAccount, capitalKnown: true);

        foreach (var side in new[] { TradeSide.Buy, TradeSide.Sell })
        {
            var blockers = EntryStateBlockers.Determine(side, Settings, snapshot, StopOutReentrySupply.NoneToday, DecisionExitReentrySupply.NoneToday, false);
            if (blocked)
                blockers.Should().Equal(RejectionReason.MaxPositionsExceeded);
            else
                blockers.Should().BeEmpty();
        }
    }

    // T-10-1784: 方向。ロングの損切りは買いの新規建て（LongSide）だけを塞ぎ、不明は返さない。
    [Fact]
    public void T_10_1784_損切りは同じ方向だけを塞ぎ不明は返さない()
    {
        var snapshot = Snapshot(0, false, false, false, false, Gfv.NotCashAccount, capitalKnown: true);

        EntryStateBlockers.Determine(
                TradeSide.Buy, Settings, snapshot, new StopOutReentrySupply(StopOutStatus.StoppedOut, StopOutStatus.None), DecisionExitReentrySupply.NoneToday, false)
            .Should().Equal(RejectionReason.StoppedOutSameDay);
        EntryStateBlockers.Determine(
                TradeSide.Sell, Settings, snapshot, new StopOutReentrySupply(StopOutStatus.StoppedOut, StopOutStatus.None), DecisionExitReentrySupply.NoneToday, false)
            .Should().BeEmpty("ロングの損切りは売りの新規建てを止めない");
        EntryStateBlockers.Determine(
                TradeSide.Sell, Settings, snapshot, new StopOutReentrySupply(StopOutStatus.None, StopOutStatus.StoppedOut), DecisionExitReentrySupply.NoneToday, false)
            .Should().Equal(RejectionReason.StoppedOutSameDay);
        EntryStateBlockers.Determine(
                TradeSide.Buy, Settings, snapshot, new StopOutReentrySupply(StopOutStatus.Unknown, StopOutStatus.Unknown), DecisionExitReentrySupply.NoneToday, false)
            .Should().BeEmpty("不明は確定した拒否ではない（審査は StopOutStatusUnknown で止める）");
    }

    // T-10-2319, #1176, IADR-0495 決定3: 判断由来の決済（利確）はその方向の新規建てだけを塞ぎ、損切りと別の名前で返す。
    [Fact]
    public void T_10_2319_判断由来の決済は同じ方向だけを塞ぎ損切りと別の名前で返す()
    {
        var snapshot = Snapshot(0, false, false, false, false, Gfv.NotCashAccount, capitalKnown: true);
        var longExited = new DecisionExitReentrySupply(LongSide: true, ShortSide: false);

        EntryStateBlockers.Determine(TradeSide.Buy, Settings, snapshot, StopOutReentrySupply.NoneToday, longExited, false)
            .Should().Equal(RejectionReason.DecisionExitSameDay);
        EntryStateBlockers.Determine(TradeSide.Sell, Settings, snapshot, StopOutReentrySupply.NoneToday, longExited, false)
            .Should().BeEmpty("ロングの利確は売りの新規建てを止めない（裁定「同じ方向」）");
        EntryStateBlockers.Determine(
                TradeSide.Sell, Settings, snapshot, StopOutReentrySupply.NoneToday, new DecisionExitReentrySupply(false, true), false)
            .Should().Equal(RejectionReason.DecisionExitSameDay);
        // 損切りと利確が同じ日に並べば両方の名前（審査の到達順）。
        EntryStateBlockers.Determine(
                TradeSide.Buy, Settings, snapshot, new StopOutReentrySupply(StopOutStatus.StoppedOut, StopOutStatus.None), longExited, false)
            .Should().Equal(RejectionReason.StoppedOutSameDay, RejectionReason.DecisionExitSameDay);
        EntryStateBlockers.Determinable.Should().Contain(RejectionReason.DecisionExitSameDay);
    }

    // T-10-1784: kill switch・一時停止・日次損失・ロックアウト・DD・GFV（現金口座で件数が既知のとき）をそれぞれ名前で返す。
    [Fact]
    public void T_10_1784_各ブロッカーを名前で返し_GFV_の件数が不明なら返さない()
    {
        static IReadOnlyList<RejectionReason> Long(PortfolioSnapshot s, bool lockedOut = false) =>
            EntryStateBlockers.Determine(TradeSide.Buy, Settings, s, StopOutReentrySupply.NoneToday, DecisionExitReentrySupply.NoneToday, lockedOut);

        var clear = Snapshot(0, false, false, false, false, Gfv.NotCashAccount, capitalKnown: true);
        Long(clear).Should().BeEmpty();
        Long(clear with { KillSwitchEngaged = true }).Should().Equal(RejectionReason.KillSwitchActive);
        Long(clear with { TradingPaused = true }).Should().Equal(RejectionReason.TradingPaused);
        Long(Snapshot(0, false, false, true, false, Gfv.NotCashAccount, true)).Should().Equal(RejectionReason.DailyLossLimitReached);
        Long(clear, lockedOut: true).Should().Equal(RejectionReason.DailyLossLimitReached);
        Long(Snapshot(0, false, false, false, true, Gfv.NotCashAccount, true)).Should().Equal(RejectionReason.MaxDrawdownReached);
        Long(Snapshot(0, false, false, false, false, Gfv.CashAtLimit, true))
            .Should().Equal(RejectionReason.GoodFaithViolationLimitReached);
        Long(Snapshot(0, false, false, false, false, Gfv.CashUnknownCount, true))
            .Should().BeEmpty("GFV の件数が未供給なのは不明（審査は止めるが確定とは言わない）");
        // 🔴 PR #1116 の監査（M6）: 信用口座（照会済み）＋件数が停止基準以上。口も審査も GFV では止めない（口と審査の一致）。
        var marginAtLimit = Snapshot(0, false, false, false, false, Gfv.MarginAtLimit, true);
        Long(marginAtLimit)
            .Should().BeEmpty("GFV は現金口座でのみ加わる統制（信用口座なら件数が停止基準以上でも塞がない）");
        RiskEvaluator.Evaluate(Entry(TradeSide.Buy), Settings, marginAtLimit, stopOuts: StopOutReentrySupply.NoneToday).Reasons
            .Should().NotContain(RejectionReason.GoodFaithViolationLimitReached, "審査も信用口座では GFV で止めない");
        Long(Snapshot(0, false, false, true, false, Gfv.NotCashAccount, capitalKnown: false))
            .Should().BeEmpty("資金が未供給なら日次損失は判定しない（審査は CapitalBaselineUnavailable で止める）");
    }

    // 🔴 T-10-2988, FR-10, FR-19, ADR-0070, ADR-0071, #1433, IADR-0572 決定1: 閉場前の締め切り・テーマの上限を供給すれば、口（Determine）は同じ入力の審査
    // （RiskEvaluator.Evaluate）と同じ理由を同じ並びで返す（述語は同じ関数。どちらかだけを直すと赤になる）。窓の中／外 × テーマの他の銘柄の保有の有無
    // × 方向 × kill switch（並びの確認）× 建玉数の上限（並びの確認）。
    [Fact]
    public void T_10_2988_締め切りとテーマを供給すれば口は審査と同じ理由を同じ並びで返す()
    {
        var themes = TradingDefaults.CreateThemeConcentration();
        var close = new DateTimeOffset(2026, 9, 23, 20, 0, 0, TimeSpan.Zero);
        var failures = new List<string>();
        var sawCutoff = false;
        var sawTheme = false;

        var cases =
            from within in new[] { false, true }
            from themeHeld in new[] { false, true }
            from side in new[] { TradeSide.Buy, TradeSide.Sell }
            from killSwitch in new[] { false, true }
            from positions in new[] { 0, MaxPositions }
            select (within, themeHeld, side, killSwitch, positions);

        foreach (var (within, themeHeld, side, killSwitch, positions) in cases)
        {
            var cutoff = new PreCloseEntryCutoffSupply(within ? close.AddMinutes(-30) : close.AddMinutes(-61), close, 60);
            var snapshot = Snapshot(positions, killSwitch, false, false, false, Gfv.NotCashAccount, capitalKnown: true) with
            {
                HeldOrPendingSymbols = themeHeld
                    ? new HashSet<(string, Market)> { ("COIN", Market.UnitedStates) }
                    : new HashSet<(string, Market)>(),
            };
            var intent = new OrderIntent("MARA", Market.UnitedStates, side, ProductType.Cash, BrokerProvider.InternalPaper, 1, 10m, PositionEffect.Open);

            var blockers = EntryStateBlockers.Determine(
                side, Settings, snapshot, StopOutReentrySupply.NoneToday, DecisionExitReentrySupply.NoneToday, lockedOut: false,
                cutoff, themes, "MARA", Market.UnitedStates);
            var screened = RiskEvaluator.Evaluate(
                    intent, Settings, snapshot, stopOuts: StopOutReentrySupply.NoneToday, decisionExits: DecisionExitReentrySupply.NoneToday,
                    preCloseCutoff: cutoff, themes: themes)
                .Reasons.Where(EntryStateBlockers.Determinable.Contains).ToList();

            if (!blockers.SequenceEqual(screened))
                failures.Add($"within={within} theme={themeHeld} side={side} kill={killSwitch} positions={positions}: 口=[{string.Join(",", blockers)}] 審査=[{string.Join(",", screened)}]");
            if (within != blockers.Contains(RejectionReason.PreCloseEntryCutoff))
                failures.Add($"締め切り within={within}: [{string.Join(",", blockers)}]");
            if (themeHeld != blockers.Contains(RejectionReason.ThemeConcentrationExceeded))
                failures.Add($"テーマ held={themeHeld}: [{string.Join(",", blockers)}]");
            sawCutoff |= within;
            sawTheme |= themeHeld;
        }

        failures.Should().BeEmpty();
        (sawCutoff && sawTheme).Should().BeTrue();
        EntryStateBlockers.Determinable.Should().Contain([RejectionReason.PreCloseEntryCutoff, RejectionReason.ThemeConcentrationExceeded]);

        // 省略（供給しない）なら判定しない（判定コアと同じ規律）。休場日（閉場が null）は窓に入らない。
        EntryStateBlockers.Determine(
                TradeSide.Buy, Settings, Snapshot(0, false, false, false, false, Gfv.NotCashAccount, true),
                StopOutReentrySupply.NoneToday, DecisionExitReentrySupply.NoneToday, false)
            .Should().BeEmpty();
        EntryStateBlockers.PreCloseCutoff(new PreCloseEntryCutoffSupply(close, SessionClose: null, 60)).Should().BeFalse();
    }
}
