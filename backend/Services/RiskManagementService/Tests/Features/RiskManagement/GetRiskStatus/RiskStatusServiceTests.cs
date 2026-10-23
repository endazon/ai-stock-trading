using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.GetRiskStatus;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, UC-07, ADR-0009: `/status` 集約の検証。3 統制の状態・優先順位・段階・当日損益・上限使用率・ポジションを束ねる。
public class RiskStatusServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 7, 9);

    private sealed class Fixture
    {
        public InMemoryKillSwitchStore KillSwitch { get; } = new();
        public InMemoryPauseStore Pause { get; } = new();
        public InMemoryLockoutStore Lockout { get; } = new();
        public InMemoryStageGateStore StageGate { get; }

        // #870, IADR-0360 決定 5: 当日のシステム外売買の取り込み件数の供給元（取り込みが無ければ 0 件）。
        public InMemoryPortfolioLedgerStore Ledger { get; } = new();
        public PortfolioState State { get; init; } = new() { LedgerEquity = 100_000m };

        // #1423 M-3: 基準資金の供給（既定は照会できている）。
        public ICapitalBaselineStore Capital { get; init; } = FakeCapitalBaseline.Of(100_000m);

        public Fixture(TradingStage stage = TradingStage.Stage0Verification)
        {
            StageGate = new InMemoryStageGateStore(stage);
        }

        public RiskStatusView Build()
        {
            var builder = new PortfolioSnapshotBuilder(
                new FakePortfolioStateProvider(State), KillSwitch, Pause,
                FakeBrokerAccountObservations.NotObserved(), FakeInformationDegradation.Affirmed(), capitalBaseline: Capital);
            var svc = new RiskStatusService(
                builder,
                new InMemoryRiskSettingsStore(),
                Pause,
                Lockout,
                StageGate,
                Ledger,
                new FakeClock(Now, Today),
                Capital);
            return svc.Build();
        }
    }

    [Fact]
    public void 統制がすべて不成立なら新規建ては停止しない()
    {
        var status = new Fixture().Build();

        status.KillSwitchEngaged.Should().BeFalse();
        status.DailyLossLockoutActive.Should().BeFalse();
        status.TradingPaused.Should().BeFalse();
        status.ActiveControl.Should().Be(ActiveTradingControl.None);
        status.NewEntriesBlocked.Should().BeFalse();
    }

    [Fact]
    public void 一時停止のみ成立なら優先中の統制は一時停止で新規建て停止()
    {
        var f = new Fixture();
        f.Pause.SetState(new PauseState(true, "user", "様子見", Now));

        var status = f.Build();

        status.TradingPaused.Should().BeTrue();
        status.ActiveControl.Should().Be(ActiveTradingControl.Pause);
        status.NewEntriesBlocked.Should().BeTrue();
    }

    [Fact]
    public void 日次損失ロックアウトのみ成立なら優先中の統制はロックアウト()
    {
        var f = new Fixture();
        f.Lockout.Set(new LockoutState(Today.AddDays(1), "日次損失上限到達", Now));

        var status = f.Build();

        status.DailyLossLockoutActive.Should().BeTrue();
        status.LockoutReleaseOn.Should().Be(Today.AddDays(1));
        status.ActiveControl.Should().Be(ActiveTradingControl.DailyLossLockout);
        status.NewEntriesBlocked.Should().BeTrue();
    }

    [Fact]
    public void 複数成立時は優先順位に従いkill_switchを最優先で示す()
    {
        // ADR-0009: 優先順位 kill switch > 日次損失ロックアウト > 一時停止。3 つ同時成立でも表示は kill switch。
        var f = new Fixture();
        f.KillSwitch.SetState(new KillSwitchState(true, "user", "緊急停止", Now));
        f.Lockout.Set(new LockoutState(Today.AddDays(1), "日次損失上限到達", Now));
        f.Pause.SetState(new PauseState(true, "user", "様子見", Now));

        var status = f.Build();

        status.ActiveControl.Should().Be(ActiveTradingControl.KillSwitch);
        status.NewEntriesBlocked.Should().BeTrue();
    }

    [Fact]
    public void ロックアウトが解除日到達済みなら成立とみなさない()
    {
        // 表示専用: ReleaseOn == Today（当日が解除日）は IsActiveOn=false（当日以降は無効）。
        var f = new Fixture();
        f.Lockout.Set(new LockoutState(Today, "前営業日の損失上限", Now.AddDays(-1)));

        var status = f.Build();

        status.DailyLossLockoutActive.Should().BeFalse();
        status.ActiveControl.Should().Be(ActiveTradingControl.None);
    }

    [Fact]
    public void 段階と当日損益とポジションを反映する()
    {
        var f = new Fixture(TradingStage.Stage1Simulate)
        {
            State = new PortfolioState
            {
                LedgerEquity = 100_000m,
                OpenPositionCount = 3,
                DailyRealizedPnl = -500m,
                UnrealizedPnl = -1_200m,
                DailyOrderedAmount = 40_000m,
                DrawdownRatio = 0.05m,
            },
        };

        var status = f.Build();

        status.Stage.Should().Be(TradingStage.Stage1Simulate);
        status.DailyRealizedPnl.Should().Be(-500m);
        status.UnrealizedPnl.Should().Be(-1_200m);
        status.DailyPnl.Should().Be(-1_700m);
        status.OpenPositionCount.Should().Be(3);
        status.DailyOrderedAmount.Should().Be(40_000m);
        status.MaxDailyOrderAmount.Should().BeGreaterThan(0m);
        status.MaxOpenPositions.Should().BeGreaterThan(0);
    }

    // ---- FR-11, SC-03, ADR-0041 決定 1, #870, IADR-0360 決定 5: 当日のシステム外売買の取り込み件数 ----

    private static LedgerDriftAdoption Adoption(DateTimeOffset adoptedAt, Market market = Market.UnitedStates) =>
        new(Guid.NewGuid(), "AAPL", market, TradeSide.Sell, 10, CostBasisPrice: 1m, FxRateToBase: 1m, LedgerQuantityBefore: 10, BrokerQuantity: 0,
            adoptedAt.AddMinutes(-10), "owner", "アプリから直接売却", adoptedAt);

    // T-10-542: 当日の取り込みだけを数える（**当日の境界は取り込みの市場の現地取引日**）。
    // Now は 2026-07-09 06:00Z＝**ET 07-09 02:00**・**JST 07-09 15:00**（いずれも取引日は 07-09）。
    [Fact]
    public void 当日のシステム外売買の取り込み件数を出す()
    {
        var f = new Fixture();
        // ET 07-09 01:00 と ET 07-09 06:00。どちらも当日。
        f.Ledger.AppendDriftAdoption(Adoption(new DateTimeOffset(2026, 7, 9, 5, 0, 0, TimeSpan.Zero)));
        f.Ledger.AppendDriftAdoption(Adoption(new DateTimeOffset(2026, 7, 9, 10, 0, 0, TimeSpan.Zero)));

        f.Build().DriftAdoptionCountToday.Should().Be(2);
    }

    // 🔴 前日の取り込みは数えない（「当期」は当日である）。
    [Fact]
    public void 前日の取り込みは当日の件数に入らない()
    {
        var f = new Fixture();
        // ET 07-08 01:00＝米国の取引日 07-08。
        f.Ledger.AppendDriftAdoption(Adoption(new DateTimeOffset(2026, 7, 8, 5, 0, 0, TimeSpan.Zero)));

        f.Build().DriftAdoptionCountToday.Should().Be(0);
    }

    // 🔴 当日の境界は**市場ごと**である。同じ瞬間でも日本市場では 07-09（当日）・米国市場では 07-08（前日）になる
    //    ——1 つの市場の暦で全件を切ると数え違える（IADR-0246 と同じ規律）。
    [Fact]
    public void 当日の境界は市場ごとに判定する()
    {
        var instant = new DateTimeOffset(2026, 7, 8, 20, 0, 0, TimeSpan.Zero); // JST 07-09 05:00 / ET 07-08 16:00
        var f = new Fixture();
        f.Ledger.AppendDriftAdoption(Adoption(instant, Market.Japan));
        f.Ledger.AppendDriftAdoption(Adoption(instant, Market.UnitedStates));

        f.Build().DriftAdoptionCountToday.Should().Be(1, "日本市場の 1 件だけが当日である");
    }

    // 🔴 0 件は「取り込みが無かった」という**事実**である（未供給ではない。台帳は常に読める）。
    [Fact]
    public void 取り込みが無ければ_0_件を出す()
    {
        new Fixture().Build().DriftAdoptionCountToday.Should().Be(0);
    }

    // 🔴 T-10-3044, FR-10, SC-03, #1423 M-3, ADR-0073 決定3: **直前の米国東部暦日の観測が欠けたために新規建てを止めていることを
    // 画面へ出す。** 基準資金は未供給（null）になり、欠けの印と最後に観測できた取引日が載る。
    [Theory]
    [InlineData(AiStockTrading.Shared.Contracts.Observability.CapitalBaselineReadOutcome.UnavailableMissingPreviousDay, true)]
    [InlineData(AiStockTrading.Shared.Contracts.Observability.CapitalBaselineReadOutcome.UnavailableStale, true)]
    [InlineData(AiStockTrading.Shared.Contracts.Observability.CapitalBaselineReadOutcome.UnavailableNoRow, false)]
    [InlineData(AiStockTrading.Shared.Contracts.Observability.CapitalBaselineReadOutcome.UnavailableNonPositive, false)]
    public void 観測の欠けで基準資金が未供給なら_その理由を表示へ載せる(
        AiStockTrading.Shared.Contracts.Observability.CapitalBaselineReadOutcome outcome, bool missing)
    {
        var capital = FakeCapitalBaseline.NotObserved();
        capital.UnavailableOutcome = outcome;
        if (outcome is AiStockTrading.Shared.Contracts.Observability.CapitalBaselineReadOutcome.UnavailableMissingPreviousDay
            or AiStockTrading.Shared.Contracts.Observability.CapitalBaselineReadOutcome.UnavailableStale)
        {
            capital.UnavailableLatestTradingDay = new DateOnly(2026, 7, 6);
        }

        var status = new Fixture { Capital = capital }.Build();

        status.Capital.Should().BeNull();
        status.CapitalBaselineObservationMissing.Should().Be(missing);
        status.CapitalBaselineLatestTradingDay.Should().Be(missing ? new DateOnly(2026, 7, 6) : null);
    }

    // T-10-3044（否定形）: 照会できている平常時は欠けを出さない。
    [Fact]
    public void 基準資金が供給されていれば欠けを出さない()
    {
        var status = new Fixture().Build();

        status.Capital.Should().Be(100_000m);
        status.CapitalBaselineObservationMissing.Should().BeFalse();
        status.CapitalBaselineLatestTradingDay.Should().BeNull();
    }

    // #1423 M-3（AI レビュー 🟡1）: 1 回目の読み出し（スナップショット）と 2 回目（表示）の間に行が書かれた状態を模す。
    // GetCurrent は供給、Read は欠けを返す（逆も）。
    private sealed class DivergingCapitalBaseline(bool currentSupplied) : ICapitalBaselineStore
    {
        private static readonly CapitalBaseline Supplied = new(100_000m, new DateOnly(2026, 7, 8), Now.AddHours(-10));

        public void Record(decimal equityInBase, DateTimeOffset observedAt)
        {
        }

        public CapitalBaseline? GetCurrent() => currentSupplied ? Supplied : null;

        public CapitalBaselineReading Read() => currentSupplied
            ? new(null, AiStockTrading.Shared.Contracts.Observability.CapitalBaselineReadOutcome.UnavailableMissingPreviousDay,
                Today, new DateOnly(2026, 7, 6), 90_000m)
            : new(Supplied, AiStockTrading.Shared.Contracts.Observability.CapitalBaselineReadOutcome.Supplied,
                Today, Supplied.TradingDay, Supplied.EquityInBase);
    }

    // 🔴 T-10-3044（続き・#1423 M-3 の AI レビュー 🟡1）: 表示の基準資金・上限の実額・欠けの印は**同じ 1 回の読み出し**から作る。
    // 読み出しの間に行が書かれても「資金あり・欠けあり」「資金なし・欠けなし」が並ばない。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 基準資金と欠けの印は同じ読み出しから作り食い違わない(bool snapshotSupplied)
    {
        var status = new Fixture { Capital = new DivergingCapitalBaseline(snapshotSupplied) }.Build();

        (status.Capital is null).Should().Be(status.CapitalBaselineObservationMissing,
            "未供給と欠けの印は同じ読み出しの結果である");
        (status.MaxOrderAmount is null).Should().Be(status.Capital is null);
        (status.MaxDailyOrderAmount is null).Should().Be(status.Capital is null);
    }
}
