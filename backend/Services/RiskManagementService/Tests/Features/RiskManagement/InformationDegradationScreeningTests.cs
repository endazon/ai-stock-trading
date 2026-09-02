using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Infrastructure.ExternalServices;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-01, FR-02, FR-10, ADR-0020 決定2/決定3, #337, #564, IADR-0249, IADR-0267:
// 情報収集の縮退状態が**発注審査（OrderScreeningService）で新規建てを止め、決済は止めない**ことの検証。
// 判定コア（RiskEvaluator）単体のテストとは別に、状態の合成（Store → SnapshotBuilder → Screen）を通す。
//
// #564: **既定は「不明なら止める」**である。ストアは有効な現況観測が無いかぎり新規建てを止めるため、
// 縮退していない状態を作るには**健全な現況観測を投入する**（＝「観測できていて健全」を明示する）。
public class InformationDegradationScreeningTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 14, 0, 0, TimeSpan.Zero);

    private static PortfolioState HealthyState => new() { Capital = 100_000m };

    private static OrderIntent Intent(PositionEffect effect) =>
        new("AAPL", Market.UnitedStates,
            effect == PositionEffect.Close ? TradeSide.Sell : TradeSide.Buy,
            ProductType.Cash, BrokerProvider.InternalPaper, 10, 1_000m, effect);

    private static TradeDecisionMade Decision(OrderIntent intent) => new(Guid.NewGuid(), intent, "テスト判断", Now);

    // 現況を観測できており、止めるものが無い状態のストア（＝平常運転）。
    private static InMemoryInformationDegradationStore ObservedHealthy()
    {
        var store = new InMemoryInformationDegradationStore(new StubTimeProvider(Now));
        store.ApplyObservation([], TimeSpan.FromHours(1), Now.AddMinutes(-1));
        return store;
    }

    private static (OrderScreeningService Service, InMemoryInformationDegradationStore Degradation) Create(
        InMemoryInformationDegradationStore? degradationStore = null)
    {
        var degradation = degradationStore ?? ObservedHealthy();
        var builder = new PortfolioSnapshotBuilder(
            new FakePortfolioStateProvider(HealthyState),
            new InMemoryKillSwitchStore(),
            new InMemoryPauseStore(),
            FakeBrokerAccountObservations.NotObserved(),
            degradation);
        var service = new OrderScreeningService(
            new InMemoryRiskSettingsStore(), builder, new InMemoryLockoutStore(),
            new FakeClock(Now, new DateOnly(2026, 7, 9)), new WeekendBusinessCalendar(),
            new InMemoryBuyInInferenceStore());
        return (service, degradation);
    }

    [Fact]
    public void 縮退中の新規建ては発注審査で拒否される()
    {
        var (service, degradation) = Create();
        degradation.MarkDegraded("news");

        var outcome = service.Screen(Decision(Intent(PositionEffect.Open)));

        outcome.IsApproved.Should().BeFalse();
        outcome.Rejected!.Reasons.Should().Contain(RejectionReason.InformationSourceDegraded);
    }

    [Fact]
    public void 縮退中でも決済は承認される_否定形()
    {
        // ADR-0020 決定2/決定3・ADR-0009: 限定縮退は手仕舞い・損切りを止めない。
        var (service, degradation) = Create();
        degradation.MarkDegraded("news");

        var outcome = service.Screen(Decision(Intent(PositionEffect.Close)));

        outcome.IsApproved.Should().BeTrue();
    }

    [Fact]
    public void 回復後の新規建ては承認される_対の肯定形()
    {
        var (service, degradation) = Create();
        degradation.MarkDegraded("news");
        degradation.MarkRecovered("news");

        var outcome = service.Screen(Decision(Intent(PositionEffect.Open)));

        outcome.IsApproved.Should().BeTrue();
    }

    [Fact]
    public void 縮退が無ければ新規建ては承認される_対の肯定形()
    {
        var (service, _) = Create();

        service.Screen(Decision(Intent(PositionEffect.Open))).IsApproved.Should().BeTrue();
    }

    // --- ストア単体の境界 ---

    [Fact]
    public void ストアはカテゴリ集合で畳み未知カテゴリの回復は無害()
    {
        var store = ObservedHealthy();

        store.BlocksNewEntries.Should().BeFalse();
        store.MarkRecovered("unknown"); // 冪等（未登録の回復は無視）
        store.BlocksNewEntries.Should().BeFalse();

        store.MarkDegraded("news");
        store.MarkDegraded("news"); // 重複登録も冪等
        store.BlocksNewEntries.Should().BeTrue();

        store.MarkRecovered("news");
        store.BlocksNewEntries.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // #564: 再起動（＝現況が不明）の扱い
    // ------------------------------------------------------------------

    // 🔴 **否定形（受け入れ基準②の本体）。** 再起動直後は現況を 1 件も受け取っていない。
    // 「縮退の記録が無い＝健全」と読むと、**欠測が続いたまま新規建てが再開する**（#564 の fail-open）。
    [Fact]
    public void 再起動直後で現況が不明なら新規建ては拒否される_否定形()
    {
        // 観測を投入しない＝プロセスを起こしたばかりの状態。
        var (service, _) = Create(new InMemoryInformationDegradationStore(new StubTimeProvider(Now)));

        var outcome = service.Screen(Decision(Intent(PositionEffect.Open)));

        outcome.IsApproved.Should().BeFalse();
        outcome.Rejected!.Reasons.Should().Contain(RejectionReason.InformationSourceDegraded);
    }

    // 🔴 **否定形の回帰（受け入れ基準③）。** 現況が不明でも**決済は止めない**。
    // 「止められない」より「閉じられない」ほうが危険であり、isEntry の短絡が構造的に担保する。
    [Fact]
    public void 現況が不明でも決済は承認される_否定形()
    {
        var (service, _) = Create(new InMemoryInformationDegradationStore(new StubTimeProvider(Now)));

        service.Screen(Decision(Intent(PositionEffect.Close))).IsApproved.Should().BeTrue();
    }

    // 🔴 **受け入れ基準①。** 遷移イベントを 1 件も受け取らなくても、収集サービスの現況観測だけで
    // 停止が**復元される**（再起動を跨いで統制が戻る）。
    [Fact]
    public void 縮退継続中の現況観測だけで新規建ての停止が復元される()
    {
        var store = new InMemoryInformationDegradationStore(new StubTimeProvider(Now));
        var (service, _) = Create(store);

        // 遷移（MarkDegraded）は使わない。次の巡回の現況観測だけを届ける。
        store.ApplyObservation(["news"], TimeSpan.FromHours(1), Now.AddMinutes(-1));

        var outcome = service.Screen(Decision(Intent(PositionEffect.Open)));

        outcome.IsApproved.Should().BeFalse();
        outcome.Rejected!.Reasons.Should().Contain(RejectionReason.InformationSourceDegraded);
    }

    // 対の肯定形: 復元された現況が「止めるものは無い」なら新規建ては通る（恒久停止にしない）。
    [Fact]
    public void 現況観測が健全なら新規建ては承認される_対の肯定形()
    {
        var store = new InMemoryInformationDegradationStore(new StubTimeProvider(Now));
        var (service, _) = Create(store);

        store.ApplyObservation([], TimeSpan.FromHours(1), Now.AddMinutes(-1));

        service.Screen(Decision(Intent(PositionEffect.Open))).IsApproved.Should().BeTrue();
    }
}
