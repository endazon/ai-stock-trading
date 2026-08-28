using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;
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
        // #375, IADR-0153 決定2: 本テストの注文は内蔵 paper（EntryIntent の Mode）であり口座種別を要求しない。
        // 観測を供給しないままにしてあるのは意図的で、発注先を moomoo へ変えた瞬間に
        // BrokerAccountTypeUnverified で落ちる（フェイルクローズが効いていることが退行検知になる）。
        var builder = new PortfolioSnapshotBuilder(
            portfolio, killSwitch, new InMemoryPauseStore(), FakeBrokerAccountObservations.NotObserved());
        // #428: 推定台帳は必須依存。本テストは強制買戻しを関心に持たないため空の台帳を渡す。
        var service = new OrderScreeningService(
            new InMemoryRiskSettingsStore(), builder, lockout, clock, new WeekendBusinessCalendar(),
            new InMemoryBuyInInferenceStore());
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

        // 翌営業日（7/10）へ進め、損益は回復済み。#249 / IADR-0246: 当日は clock.UtcNow から
        // 注文の市場（米国東部時間）の現地取引日として導出されるため、UtcNow を進める。
        clock.UtcNow = new DateTimeOffset(2026, 7, 10, 15, 0, 0, TimeSpan.Zero); // ET 7/10 金 11:00
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
        // #375, IADR-0153 決定2: 本テストの注文は内蔵 paper（EntryIntent の Mode）であり口座種別を要求しない。
        // 観測を供給しないままにしてあるのは意図的で、発注先を moomoo へ変えた瞬間に
        // BrokerAccountTypeUnverified で落ちる（フェイルクローズが効いていることが退行検知になる）。
        var builder = new PortfolioSnapshotBuilder(
            portfolio, killSwitch, new InMemoryPauseStore(), FakeBrokerAccountObservations.NotObserved());
        // #428: 推定台帳は必須依存。本テストは強制買戻しを関心に持たないため空の台帳を渡す。
        var service = new OrderScreeningService(
            new InMemoryRiskSettingsStore(), builder, lockout, clock, new WeekendBusinessCalendar(),
            new InMemoryBuyInInferenceStore());

        service.Screen(Decision(EntryIntent())); // 金曜に到達
        lockout.Get()!.ReleaseOn.Should().Be(new DateOnly(2026, 7, 13)); // 月曜

        // 土曜に回復しても継続（#249: 当日は UtcNow から市場現地取引日で導出する）。
        clock.UtcNow = new DateTimeOffset(2026, 7, 11, 15, 0, 0, TimeSpan.Zero); // ET 7/11 土
        clock.Today = new DateOnly(2026, 7, 11);
        portfolio.State = HealthyState;
        service.Screen(Decision(EntryIntent())).IsApproved.Should().BeFalse();

        // 月曜に解除。
        clock.UtcNow = new DateTimeOffset(2026, 7, 13, 15, 0, 0, TimeSpan.Zero); // ET 7/13 月
        clock.Today = new DateOnly(2026, 7, 13);
        service.Screen(Decision(EntryIntent())).IsApproved.Should().BeTrue();
    }

    // #337（#249 吸収）, IADR-0246 の否定形: JST の日付が変わっても、米国市場の**同一セッション中**は
    // 日次損失ロックアウトが解除されない。JST 固定の従来実装では ET 10-11 時（JST 0 時）に
    // 「翌日」となり、デイリーストップが同一セッションの途中で外れていた。
    [Fact]
    public void 米国セッション中にJSTの日付が変わってもロックアウトは解除されない()
    {
        // ET 7/9（木）10:00 = JST 7/9 23:00 に日次損失上限へ到達。
        var clock = new FakeClock(new DateTimeOffset(2026, 7, 9, 14, 0, 0, TimeSpan.Zero), new DateOnly(2026, 7, 9));
        var portfolio = new FakePortfolioStateProvider(HealthyState with { DailyRealizedPnl = -2_000m });
        var lockout = new InMemoryLockoutStore();
        var builder = new PortfolioSnapshotBuilder(
            portfolio, new InMemoryKillSwitchStore(), new InMemoryPauseStore(),
            FakeBrokerAccountObservations.NotObserved());
        var service = new OrderScreeningService(
            new InMemoryRiskSettingsStore(), builder, lockout, clock, new WeekendBusinessCalendar(),
            new InMemoryBuyInInferenceStore());

        service.Screen(Decision(EntryIntent()));
        lockout.Get()!.ReleaseOn.Should().Be(new DateOnly(2026, 7, 10)); // 翌営業日（ET 基準）

        // 90 分後 = ET 7/9 11:30（同一セッション）。JST では 7/10 0:30 ＝ 日付が既に変わっている。
        clock.UtcNow = new DateTimeOffset(2026, 7, 9, 15, 30, 0, TimeSpan.Zero);
        clock.Today = new DateOnly(2026, 7, 10); // JST 基準の「当日」は翌日へ進んでいる
        portfolio.State = HealthyState;

        // それでも米国市場の現地取引日は 7/9 のままであり、新規建ては拒否され続ける。
        var outcome = service.Screen(Decision(EntryIntent()));
        outcome.IsApproved.Should().BeFalse();
        outcome.Rejected!.Reasons.Should().Contain(RejectionReason.DailyLossLimitReached);

        // 手仕舞いは同じ状況でも止まらない（ADR-0009 の不変条件）。
        service.Screen(Decision(EntryIntent(PositionEffect.Close))).IsApproved.Should().BeTrue();
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

    // FR-20, FR-11, #387, IADR-0148 決定3: 審査結果は**承認でも拒否でも**観測を伴う。
    // 拒否だけを観測すると「違反 0 件」を主張する根拠が無くなり、未供給と区別できない。
    [Fact]
    public void 承認された審査も観測を伴う()
    {
        var (service, _, _, _, _) = CreateService();
        var decision = Decision(EntryIntent());

        var outcome = service.Screen(decision);

        outcome.IsApproved.Should().BeTrue();
        outcome.Observation.DecisionId.Should().Be(decision.DecisionId);
        outcome.Observation.Provider.Should().Be(BrokerProvider.InternalPaper);
        outcome.Observation.RejectionReasons.Should().BeEmpty();
    }

    // FR-20, FR-11, #387: クラス C（禁止銘柄）の拒否が観測から 1 件として集計される。
    // 発注先は**その注文が向いていた先**であり、算入対象（moomoo SIMULATE）なら件数に入る。
    [Fact]
    public void 禁止銘柄の拒否はクラスC統制違反1件として集計される()
    {
        var (service, _, _, _, _) = CreateService();
        // 既定の禁止銘柄「6457」（Market.Japan）。TradingDefaults が単一情報源。
        var intent = new OrderIntent(
            "6457", Market.Japan, TradeSide.Buy, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 1, 1_000m);

        var outcome = service.Screen(Decision(intent));

        outcome.IsApproved.Should().BeFalse();
        outcome.Observation.RejectionReasons.Should().Contain(RejectionReason.BannedSymbol);

        var tally = ControlViolationAggregation.Tally([outcome.Observation]);
        tally.Should().NotBeNull();
        tally!.Count.Should().Be(1);
    }

    // **否定形**（§4.1）: クラス B（緊急停止中）の拒否は件数を増やさない。
    // ただし審査は動いている＝**集計は供給されている**（0 件を主張できる）。
    [Fact]
    public void 緊急停止による拒否は供給を作るが件数は増やさない()
    {
        var (service, _, _, killSwitch, _) = CreateService();
        killSwitch.SetState(new KillSwitchState(true, "user", "緊急停止", Now));
        var intent = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 10, 1_000m);

        var outcome = service.Screen(Decision(intent));

        outcome.IsApproved.Should().BeFalse();
        var tally = ControlViolationAggregation.Tally([outcome.Observation]);
        tally.Should().NotBeNull("審査が動いている＝集計は供給されている");
        tally!.Count.Should().Be(0, "クラス B は「統制違反 0 件」に計上しない");
    }
}
