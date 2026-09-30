using RiskManagementService.Common.Abstractions;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.GetEntryBlockers;
using RiskManagementService.Infrastructure.ExternalServices;
using RiskManagementService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// 🔴 T-10-1783, FR-10, FR-04, #1113, IADR-0463 決定 2・3: 新規建ての可否の口（EntryBlockersService）が、**同じストアの上の
// 審査（OrderScreeningService）**と同じ答えを返すこと。判定コアの外にある入力（台帳からの当日の損切り・日次損失のロックアウト・
// kill switch・一時停止のストア）の組み立てまで含めて一致させる（組み立てを口の側で別に書くと、ここが赤になる）。
public class EntryBlockersServiceTests
{
    // 2026-09-23 10:00 EDT（米国の取引日の中）。
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 14, 0, 0, TimeSpan.Zero);

    private sealed class Fixture(DateTimeOffset? now = null)
    {
        public InMemoryPortfolioLedgerStore Ledger { get; } = new();
        public InMemoryKillSwitchStore KillSwitch { get; } = new();
        public InMemoryPauseStore Pause { get; } = new();
        public InMemoryLockoutStore Lockout { get; } = new();
        public InMemoryRiskSettingsStore Settings { get; } = new();
        public IClock Clock { get; } = new FakeClock(now ?? Now, TradingDay.Of(now ?? Now));

        private PortfolioSnapshotBuilder Snapshots() => new(
            new LedgerPortfolioStateProvider(
                Ledger, new InMemoryWorkingEntryOrderSource(Ledger, new InMemoryOrderActivityStore()), Clock),
            KillSwitch, Pause, new InMemoryBrokerAccountObservationStore(TimeProvider.System),
            FakeInformationDegradation.Affirmed(), capitalBaseline: FakeCapitalBaseline.Of(100_000m));

        public EntryBlockersService Blockers() => new(Settings, Snapshots(), Lockout, Ledger, Clock);

        public OrderScreeningService Screening() => new(
            Settings, Snapshots(), Lockout, Clock, new WeekendBusinessCalendar(), new InMemoryBuyInInferenceStore(),
            Ledger, TestShortSellContexts.Unavailable(Clock, Ledger));
    }

    private static TradeDecisionMade Buy() =>
        new(Guid.NewGuid(),
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
                1, 100m, PositionEffect.Open, StopLossPrice: 95m),
            "判断", Now);

    // 審査の拒否理由のうち、口の対象になるもの（並びを保つ）。
    private static async Task<IReadOnlyList<RejectionReason>> ScreenedDeterminableAsync(Fixture f)
    {
        var outcome = await f.Screening().ScreenAsync(Buy());
        return outcome.IsApproved
            ? []
            : [.. outcome.Rejected!.Reasons.Where(EntryStateBlockers.Determinable.Contains)];
    }

    public static TheoryData<string> Scenarios() => new()
    {
        "clear", "kill-switch", "pause", "lockout", "lockout-expired", "stopped-out-s1", "stopped-out-unknown",
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task T_10_1783_口は同じストアの上の審査と同じ理由を返す(string scenario)
    {
        var f = new Fixture();
        switch (scenario)
        {
            case "kill-switch":
                f.KillSwitch.SetState(new KillSwitchState(true, "owner", "試験", Now));
                break;
            case "pause":
                f.Pause.SetState(new PauseState(true, "owner", "試験", Now));
                break;
            case "lockout":
                f.Lockout.Set(new LockoutState(TradingDay.Of(Now, Market.UnitedStates).AddDays(1), "試験", Now));
                break;
            case "lockout-expired":
                f.Lockout.Set(new LockoutState(TradingDay.Of(Now, Market.UnitedStates), "試験", Now.AddDays(-1)));
                break;
            case "stopped-out-s1":
                f.Ledger.AppendApproval(Guid.NewGuid(), SellClose(), Now.AddMinutes(-3), source: ApprovalSource.SoftwareStopS1);
                break;
            case "stopped-out-unknown":
                f.Ledger.AppendApproval(Guid.NewGuid(), SellClose(), Now.AddMinutes(-3), source: null);
                break;
        }

        // 口を先に読む（口は状態を書き換えない。審査より前に読んでも後の審査の答えが変わらない）。
        var view = f.Blockers().Build("AAPL", Market.UnitedStates);
        var screened = await ScreenedDeterminableAsync(f);

        view.LongSide.Should().Equal(screened);
        view.Symbol.Should().Be("AAPL");
        view.Market.Should().Be(Market.UnitedStates);
        var expected = scenario switch
        {
            "kill-switch" => [RejectionReason.KillSwitchActive],
            "pause" => [RejectionReason.TradingPaused],
            "lockout" => [RejectionReason.DailyLossLimitReached],
            "stopped-out-s1" => [RejectionReason.StoppedOutSameDay],
            _ => Array.Empty<RejectionReason>(),
        };
        view.LongSide.Should().Equal(expected);
    }

    // T-10-1783: 方向。ロングの損切りは売りの新規建て（ShortSide）を塞がない。状態（kill switch）は両方向を塞ぐ。
    [Fact]
    public void T_10_1783_方向別に返す()
    {
        var f = new Fixture();
        f.Ledger.AppendApproval(Guid.NewGuid(), SellClose(), Now.AddMinutes(-3), source: ApprovalSource.SoftwareStopS1);

        var stopped = f.Blockers().Build("AAPL", Market.UnitedStates);
        stopped.LongSide.Should().Equal(RejectionReason.StoppedOutSameDay);
        stopped.ShortSide.Should().BeEmpty();

        f.KillSwitch.SetState(new KillSwitchState(true, "owner", "試験", Now));
        var killed = f.Blockers().Build("AAPL", Market.UnitedStates);
        killed.LongSide.Should().Equal(RejectionReason.KillSwitchActive, RejectionReason.StoppedOutSameDay);
        killed.ShortSide.Should().Equal(RejectionReason.KillSwitchActive);

        f.Blockers().Build("MSFT", Market.UnitedStates).LongSide
            .Should().Equal([RejectionReason.KillSwitchActive], "損切りは銘柄単位（別の銘柄には及ばない）");
    }

    // T-10-1783: 🔴 口は状態を書き換えない（失効したロックアウトを掃除するのは審査だけ）。
    [Fact]
    public void T_10_1783_口は失効したロックアウトを掃除しない()
    {
        var f = new Fixture();
        var expired = new LockoutState(TradingDay.Of(Now, Market.UnitedStates), "試験", Now.AddDays(-1));
        f.Lockout.Set(expired);

        f.Blockers().Build("AAPL", Market.UnitedStates).LongSide.Should().BeEmpty();

        f.Lockout.Get().Should().Be(expired);
    }

    // T-10-1783: 🔴 当日は**銘柄の市場の現地取引日**で読む（PR #1116 の監査 M5。審査側の同型は OrderScreeningServiceTests の
    // 「米国セッション中にJSTの日付が変わってもロックアウトは解除されない」）。ET 9/23 11:30 ＝ JST 9/24 0:30 に、
    // ET 9/23 に到達して 9/24 解除のロックアウトは米国の当日にまだ有効である。JST の日付で読むと失効に見えて口が空を返す。
    [Fact]
    public async Task T_10_1783_JSTの日付が変わっても米国の取引日内ならロックアウトを返す()
    {
        var jstNextDay = new DateTimeOffset(2026, 9, 23, 15, 30, 0, TimeSpan.Zero);
        var f = new Fixture(jstNextDay);
        f.Lockout.Set(new LockoutState(new DateOnly(2026, 9, 24), "試験", Now));
        TradingDay.Of(jstNextDay, Market.Japan).Should().Be(new DateOnly(2026, 9, 24), "前提: JST では日付が変わっている");
        TradingDay.Of(jstNextDay, Market.UnitedStates).Should().Be(new DateOnly(2026, 9, 23), "前提: 米国の取引日内");

        var view = f.Blockers().Build("AAPL", Market.UnitedStates);

        view.LongSide.Should().Equal(RejectionReason.DailyLossLimitReached);
        view.ShortSide.Should().Equal(RejectionReason.DailyLossLimitReached);
        view.LongSide.Should().Equal(await ScreenedDeterminableAsync(f), "審査も米国の当日で読む（口と審査の一致）");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void T_10_1783_銘柄の欠落は引数の誤り(string symbol)
    {
        var act = () => new Fixture().Blockers().Build(symbol, Market.UnitedStates);
        act.Should().Throw<ArgumentException>();
    }

    private static OrderIntent SellClose() =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.InternalPaper,
            7, 99m, PositionEffect.Close, MarketOrder: true);
}
