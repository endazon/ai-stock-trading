using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-10, UC-06, ADR-0009, ADR-0003, #329 第 2 段階: 取引停止の 3 統制
//（kill switch ＞ 日次損失ロックアウト ＞ 一時停止）の優先順位と共通不変条件。
//
// 計画（FR-10・UC-06）が定める規則は 3 つである。
//   (a) いずれか 1 つでも成立していれば**新規建てを停止する**（判定は OR）
//   (b) 表示の優先順位は **kill switch ＞ 日次損失ロックアウト ＞ 一時停止**（重い順）
//   (c) いずれの統制も**手仕舞い（Close）と損切りは止めない**
//
// テスト戦略（docs/tests/README.md §2）の 3 点セットで構成する。
//   1. 境界値テーブル — 3 統制の成立/不成立 **8 通りすべて**（優先順位の切り替わり点を漏れなく見る）
//   2. プロパティベース — 8 通りで常に成り立つ不変条件（OR ＝ 停止・最優先は重い順・None は不成立時のみ）
//   3. 否定形 — 統制を迂回できないこと／統制が手仕舞いを塞がないこと（resume は他統制を解除しない等）
public class TradingControlPriorityTests
{
    private static readonly DateOnly Today = new(2026, 8, 4);
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 6, 0, 0, TimeSpan.Zero);

    private sealed class Fixture(bool killSwitch = false, bool lockout = false, bool paused = false)
    {
        public InMemoryKillSwitchStore KillSwitch { get; } = Create(killSwitch);
        public InMemoryPauseStore Pause { get; } = CreatePause(paused);
        public InMemoryLockoutStore Lockout { get; } = CreateLockout(lockout);

        private static InMemoryKillSwitchStore Create(bool engaged)
        {
            var store = new InMemoryKillSwitchStore();
            if (engaged)
            {
                store.SetState(new KillSwitchState(true, "user", "緊急停止", Now));
            }

            return store;
        }

        private static InMemoryPauseStore CreatePause(bool paused)
        {
            var store = new InMemoryPauseStore();
            if (paused)
            {
                store.SetState(new PauseState(true, "user", "様子見", Now));
            }

            return store;
        }

        private static InMemoryLockoutStore CreateLockout(bool active)
        {
            var store = new InMemoryLockoutStore();
            if (active)
            {
                store.Set(new LockoutState(Today.AddDays(1), "日次損失上限到達", Now));
            }

            return store;
        }

        private PortfolioSnapshotBuilder Builder() => new(
            new FakePortfolioStateProvider(new PortfolioState { Capital = 100_000m }),
            KillSwitch,
            Pause,
            // 本テストの注文は内蔵 paper であり口座種別を要求しない（IADR-0153 決定2）。
            FakeBrokerAccountObservations.NotObserved(),
            new InMemoryInformationDegradationStore());

        public RiskStatusView Status() => new RiskStatusService(
            Builder(),
            new InMemoryRiskSettingsStore(),
            Pause,
            Lockout,
            new InMemoryStageGateStore(TradingStage.Stage0Verification),
            new FakeClock(Now, Today)).Build();

        public ScreeningOutcome Screen(PositionEffect effect)
        {
            // #428: 推定台帳は必須依存。本テストは強制買戻しを関心に持たないため空の台帳を渡す。
            var service = new OrderScreeningService(
                new InMemoryRiskSettingsStore(),
                Builder(),
                Lockout,
                new FakeClock(Now, Today),
                new WeekendBusinessCalendar(),
                new InMemoryBuyInInferenceStore());
            var intent = new OrderIntent(
                "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
                10, 1_000m, effect);
            return service.Screen(new TradeDecisionMade(Guid.NewGuid(), intent, "テスト判断", Now));
        }
    }

    // ------------------------------------------------------------------
    // 1. 境界値テーブル（3 統制の成立/不成立 8 通り）
    // ------------------------------------------------------------------

    // FR-10, UC-06, ADR-0009: 優先順位 kill switch ＞ 日次損失ロックアウト ＞ 一時停止。
    // 表示は**成立中で最も重い統制**を示し、いずれか成立していれば新規建てを止める。
    [Theory]
    [InlineData(false, false, false, ActiveTradingControl.None, false)]
    [InlineData(false, false, true, ActiveTradingControl.Pause, true)]
    [InlineData(false, true, false, ActiveTradingControl.DailyLossLockout, true)]
    [InlineData(false, true, true, ActiveTradingControl.DailyLossLockout, true)]
    [InlineData(true, false, false, ActiveTradingControl.KillSwitch, true)]
    [InlineData(true, false, true, ActiveTradingControl.KillSwitch, true)]
    [InlineData(true, true, false, ActiveTradingControl.KillSwitch, true)]
    [InlineData(true, true, true, ActiveTradingControl.KillSwitch, true)]
    public void 三統制の優先順位と新規建て停止は成立の組み合わせで決まる(
        bool killSwitch, bool lockout, bool paused,
        ActiveTradingControl expectedActive, bool expectedBlocked)
    {
        var status = new Fixture(killSwitch, lockout, paused).Status();

        status.ActiveControl.Should().Be(expectedActive);
        status.NewEntriesBlocked.Should().Be(expectedBlocked);
        status.KillSwitchEngaged.Should().Be(killSwitch);
        status.DailyLossLockoutActive.Should().Be(lockout);
        status.TradingPaused.Should().Be(paused);
    }

    // ------------------------------------------------------------------
    // 2. プロパティベース（8 通りで常に成り立つ不変条件）
    // ------------------------------------------------------------------

    public static TheoryData<bool, bool, bool> AllCombinations()
    {
        var data = new TheoryData<bool, bool, bool>();
        foreach (var kill in new[] { false, true })
        {
            foreach (var lockout in new[] { false, true })
            {
                foreach (var paused in new[] { false, true })
                {
                    data.Add(kill, lockout, paused);
                }
            }
        }

        return data;
    }

    // FR-10「いずれか1つでも成立していれば新規建てを停止し」＝ 判定は OR（優先順位は表示のためのもの）。
    // 優先順位で 1 つに絞られた結果、他の統制が「効かなくなる」ことがないことを固定する。
    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void 新規建て停止は三統制のORであり優先順位は表示にのみ効く(
        bool killSwitch, bool lockout, bool paused)
    {
        var status = new Fixture(killSwitch, lockout, paused).Status();

        status.NewEntriesBlocked.Should().Be(killSwitch || lockout || paused);
        // 最優先の統制は「成立している統制のうち最も重いもの」であり、不成立の統制を指さない。
        status.ActiveControl.Should().Be(
            killSwitch ? ActiveTradingControl.KillSwitch
            : lockout ? ActiveTradingControl.DailyLossLockout
            : paused ? ActiveTradingControl.Pause
            : ActiveTradingControl.None);
        // None は「1 つも成立していない」ときに限る（逆も成り立つ）。
        (status.ActiveControl == ActiveTradingControl.None).Should().Be(!status.NewEntriesBlocked);
    }

    // FR-10, ADR-0009, ADR-0003 の共通不変条件:「いずれの統制も新規建て（エントリー）のみを止め、
    // **手仕舞い（Close）と損切りは止めない**」。8 通りすべてで成立する。
    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void どの統制が成立していても手仕舞いは止まらない(bool killSwitch, bool lockout, bool paused)
    {
        var outcome = new Fixture(killSwitch, lockout, paused).Screen(PositionEffect.Close);

        outcome.IsApproved.Should().BeTrue();
        outcome.Rejected.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // 3. 否定形（統制を迂回できないこと）
    // ------------------------------------------------------------------

    // FR-10, UC-06: いずれか 1 つでも成立していれば新規建ては通らない。
    // 「軽い統制（pause）だけなら通る」「重い統制が表示されている間は軽い統制が効かない」という
    // 抜けが無いことを、成立の組み合わせすべてで固定する。
    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void 統制がひとつでも成立していれば新規建ては通らない(
        bool killSwitch, bool lockout, bool paused)
    {
        var outcome = new Fixture(killSwitch, lockout, paused).Screen(PositionEffect.Open);

        outcome.IsApproved.Should().Be(!(killSwitch || lockout || paused));
    }

    // FR-10, UC-06, ADR-0009:「再開（resume）は一時停止のみを解除する。kill switch と
    // 日次損失ロックアウトは解除しない」。resume を迂回路にして停止を抜けられないことを固定する。
    [Fact]
    public void 再開は一時停止のみを解除し他の二統制を解除しない()
    {
        var f = new Fixture(killSwitch: true, lockout: true, paused: true);

        f.Pause.SetState(new PauseState(false, "user", "再開", Now));

        var status = f.Status();
        status.TradingPaused.Should().BeFalse();
        status.KillSwitchEngaged.Should().BeTrue();
        status.DailyLossLockoutActive.Should().BeTrue();
        status.ActiveControl.Should().Be(ActiveTradingControl.KillSwitch);
        f.Screen(PositionEffect.Open).IsApproved.Should().BeFalse();
    }

    // FR-10, UC-06, IADR-0008: 日次損失ロックアウトは**機械的解除のみ**（翌営業日まで）。
    // 一時停止を解除しても、kill switch を解除しても、ロックアウトは残り新規建ては通らない。
    [Fact]
    public void 日次損失ロックアウトは他統制の解除では抜けられない()
    {
        var f = new Fixture(killSwitch: true, lockout: true, paused: true);

        f.Pause.SetState(new PauseState(false, "user", "再開", Now));
        f.KillSwitch.SetState(new KillSwitchState(false, "user", "解除", Now));

        var status = f.Status();
        status.ActiveControl.Should().Be(ActiveTradingControl.DailyLossLockout);
        status.NewEntriesBlocked.Should().BeTrue();

        var outcome = f.Screen(PositionEffect.Open);
        outcome.IsApproved.Should().BeFalse();
        outcome.Rejected!.Reasons.Should().Contain(RejectionReason.DailyLossLimitReached);
    }

    // FR-20, ADR-0016 決定10, 06_daytrading-review §4.1: 3 統制による拒否は
    // **クラス B（緊急停止中の拒否）またはクラス A（損失上限到達＝統制の正常作動）**であり、
    // 「統制違反 0 件」（クラス C 限定）には計上しない。停止統制の作動を AI の違反として数えると、
    // 段階昇格ゲートが恒久ブロックになる。
    [Fact]
    public void 三統制による拒否は統制違反に計上しない()
    {
        var outcome = new Fixture(killSwitch: true, lockout: true, paused: true)
            .Screen(PositionEffect.Open);

        outcome.Rejected!.Reasons.Should().Contain(RejectionReason.KillSwitchActive);
        outcome.Rejected.Reasons.Should().Contain(RejectionReason.TradingPaused);
        outcome.Rejected.Reasons.Should().Contain(RejectionReason.DailyLossLimitReached);
        RejectionReasonClassification.CountsAsControlViolation(outcome.Rejected.Reasons)
            .Should().BeFalse();
        outcome.Rejected.Reasons.Should().NotContain(
            r => RejectionReasonClassification.ClassOf(r) == RejectionReasonClass.C);
    }
}
