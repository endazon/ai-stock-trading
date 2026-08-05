using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-10, FR-19, FR-20, UC-01, UC-02, ADR-0003, IADR-0008: スクリーニング・オーケストレーションの検証。
// kill switch のエントリー限定停止・日次損失ロックアウトの当日維持と翌営業日解除・手仕舞いのフェイルセーフを固定する。
public class OrderScreeningServiceTests
{
    private static readonly DateOnly TradingDay = new(2026, 7, 9); // 木曜
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 6, 0, 0, TimeSpan.Zero);

    // 既定で全統制を通過する状態（資金 10 万・損益ゼロ・保有なし）。
    private static PortfolioState HealthyState => new()
    {
        Capital = 100_000m,
        OpenPositionCount = 0,
        InvestedCapital = 0m,
        DailyOrderedAmount = 0m,
        DailyRealizedPnl = 0m,
        UnrealizedPnl = 0m,
    };

    // Stage0（Paper）で全統制を通過する新規建て注文。10 株 × 1,000 円 = 10,000 円。
    private static OrderIntent EntryIntent(PositionEffect effect = PositionEffect.Open) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 1_000m, effect);

    private static TradeDecisionMade Decision(OrderIntent intent) =>
        new(Guid.NewGuid(), intent, "テスト判断", Now);

    private static (OrderScreeningService Service, FakeClock Clock, FakePortfolioStateProvider Portfolio,
        InMemoryKillSwitchStore KillSwitch, InMemoryLockoutStore Lockout) CreateService(
        PortfolioState? state = null)
    {
        var clock = new FakeClock(Now, TradingDay);
        var portfolio = new FakePortfolioStateProvider(state ?? HealthyState);
        var killSwitch = new InMemoryKillSwitchStore();
        var lockout = new InMemoryLockoutStore();
        var builder = new PortfolioSnapshotBuilder(portfolio, killSwitch, new InMemoryPauseStore());
        var service = new OrderScreeningService(
            new InMemoryRiskSettingsStore(), builder, lockout, clock, new WeekendBusinessCalendar());
        return (service, clock, portfolio, killSwitch, lockout);
    }

    [Fact]
    public void 統制を通過する新規建ては承認され承認数量を伴う()
    {
        var (service, _, _, _, _) = CreateService();
        var intent = EntryIntent();

        var outcome = service.Screen(Decision(intent));

        outcome.IsApproved.Should().BeTrue();
        outcome.Approved.Should().NotBeNull();
        outcome.Approved!.ApprovedQuantity.Should().Be(10);
        outcome.Approved.Intent.Should().Be(intent);
        outcome.Rejected.Should().BeNull();
    }

    [Fact]
    public void kill_switch_起動中の新規建ては拒否される()
    {
        // FR-10, ADR-0003: kill switch 起動後、新規発注（エントリー）は一切通らない。
        var (service, _, _, killSwitch, _) = CreateService();
        killSwitch.SetState(new KillSwitchState(true, "user", "緊急停止", Now));

        var outcome = service.Screen(Decision(EntryIntent()));

        outcome.IsApproved.Should().BeFalse();
        outcome.Rejected!.Reasons.Should().Contain(RejectionReason.KillSwitchActive);
    }

    [Fact]
    public void kill_switch_起動中でも手仕舞いは承認される()
    {
        // ADR-0003 フェイルセーフ: 保有ポジションの手仕舞い（損切り含む）は kill switch でも止めない。
        var (service, _, _, killSwitch, _) = CreateService();
        killSwitch.SetState(new KillSwitchState(true, "user", "緊急停止", Now));

        var outcome = service.Screen(Decision(EntryIntent(PositionEffect.Close)));

        outcome.IsApproved.Should().BeTrue();
    }

    [Fact]
    public void 日次損失上限到達で拒否されロックアウトが設定される()
    {
        // IADR-0008: 実現 -2,000 円 = 資金 10 万の 2%。到達で当日ロックアウト（翌営業日まで）。
        var state = HealthyState with { DailyRealizedPnl = -2_000m };
        var (service, _, _, _, lockout) = CreateService(state);

        var outcome = service.Screen(Decision(EntryIntent()));

        outcome.IsApproved.Should().BeFalse();
        outcome.Rejected!.Reasons.Should().Contain(RejectionReason.DailyLossLimitReached);
        lockout.Get().Should().NotBeNull();
        lockout.Get()!.ReleaseOn.Should().Be(new DateOnly(2026, 7, 10)); // 翌営業日（金曜）
    }

    [Fact]
    public void ロックアウトは損益が回復しても当日中は新規建てを止め続ける()
    {
        // デイリーストップ: 一度到達したら含み損・実現損が回復しても当日は翌営業日までロックする。
        var state = HealthyState with { DailyRealizedPnl = -2_000m };
        var (service, _, portfolio, _, _) = CreateService(state);

        // 1 回目で到達 → ロックアウト設定。
        service.Screen(Decision(EntryIntent())).IsApproved.Should().BeFalse();

        // 損益が回復（実現ゼロ）しても、同日中の新規建ては拒否され続ける。
        portfolio.State = HealthyState;
        var outcome = service.Screen(Decision(EntryIntent()));

        outcome.IsApproved.Should().BeFalse();
        outcome.Rejected!.Reasons.Should().Contain(RejectionReason.DailyLossLimitReached);
    }

    [Fact]
    public void ロックアウト中でも手仕舞いは承認される()
    {
        // フェイルセーフ: 損失局面での手仕舞い（損切り）はロックアウト中でも止めない。
        var state = HealthyState with { DailyRealizedPnl = -2_000m };
        var (service, _, portfolio, _, _) = CreateService(state);
        service.Screen(Decision(EntryIntent())); // ロックアウト設定

        portfolio.State = HealthyState;
        var outcome = service.Screen(Decision(EntryIntent(PositionEffect.Close)));

        outcome.IsApproved.Should().BeTrue();
    }

    [Fact]
    public void ロックアウトは翌営業日に解除され新規建てが再び可能になる()
    {
        // IADR-0008: 翌営業日（IBusinessCalendar）に達したらロックアウトは失効する。
        var state = HealthyState with { DailyRealizedPnl = -2_000m };
        var (service, clock, portfolio, _, lockout) = CreateService(state);
        service.Screen(Decision(EntryIntent())); // 7/9 に到達 → 7/10 解除予定

        // 翌営業日（7/10）へ進め、損益は回復済み。
        clock.Today = new DateOnly(2026, 7, 10);
        portfolio.State = HealthyState;
        var outcome = service.Screen(Decision(EntryIntent()));

        outcome.IsApproved.Should().BeTrue();
        lockout.Get().Should().BeNull(); // 失効時に掃除される
    }

    [Fact]
    public void 金曜到達のロックアウトは翌月曜まで継続する()
    {
        // 週末スキップ: 金曜（7/10）到達なら解除は翌営業日の月曜（7/13）。土日は新規建て不可。
        var clock = new FakeClock(new DateTimeOffset(2026, 7, 10, 6, 0, 0, TimeSpan.Zero), new DateOnly(2026, 7, 10));
        var portfolio = new FakePortfolioStateProvider(HealthyState with { DailyRealizedPnl = -2_000m });
        var killSwitch = new InMemoryKillSwitchStore();
        var lockout = new InMemoryLockoutStore();
        var builder = new PortfolioSnapshotBuilder(portfolio, killSwitch, new InMemoryPauseStore());
        var service = new OrderScreeningService(
            new InMemoryRiskSettingsStore(), builder, lockout, clock, new WeekendBusinessCalendar());

        service.Screen(Decision(EntryIntent())); // 金曜に到達
        lockout.Get()!.ReleaseOn.Should().Be(new DateOnly(2026, 7, 13)); // 月曜

        // 土曜に回復しても継続。
        clock.Today = new DateOnly(2026, 7, 11);
        portfolio.State = HealthyState;
        service.Screen(Decision(EntryIntent())).IsApproved.Should().BeFalse();

        // 月曜に解除。
        clock.Today = new DateOnly(2026, 7, 13);
        service.Screen(Decision(EntryIntent())).IsApproved.Should().BeTrue();
    }

    [Fact]
    public void 拒否イベントは判断ID_注文意図_理由_日時を伴う()
    {
        // FR-11: 監査・通知のため拒否イベントに必要な情報を載せる。
        var (service, clock, _, killSwitch, _) = CreateService();
        killSwitch.SetState(new KillSwitchState(true, "user", "停止", Now));
        var intent = EntryIntent();
        var decision = Decision(intent);

        var outcome = service.Screen(decision);

        var rejected = outcome.Rejected!;
        rejected.DecisionId.Should().Be(decision.DecisionId);
        rejected.Intent.Should().Be(intent);
        rejected.RejectedAt.Should().Be(clock.UtcNow);
        rejected.Reasons.Should().NotBeEmpty();
    }
}
