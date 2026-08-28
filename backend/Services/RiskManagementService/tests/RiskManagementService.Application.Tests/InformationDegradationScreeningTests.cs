using RiskManagementService.Application.Adapters;
using RiskManagementService.Application.Services;
using RiskManagementService.Application.State;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Application.Tests;

// FR-01, FR-02, FR-10, ADR-0020 決定2/決定3, #337, IADR-0249:
// 情報収集の縮退状態が**発注審査（OrderScreeningService）で新規建てを止め、決済は止めない**ことの検証。
// 判定コア（RiskEvaluator）単体のテストとは別に、状態の合成（Store → SnapshotBuilder → Screen）を通す。
public class InformationDegradationScreeningTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 14, 0, 0, TimeSpan.Zero);

    private static PortfolioState HealthyState => new() { Capital = 100_000m };

    private static OrderIntent Intent(PositionEffect effect) =>
        new("AAPL", Market.UnitedStates,
            effect == PositionEffect.Close ? TradeSide.Sell : TradeSide.Buy,
            ProductType.Cash, BrokerProvider.InternalPaper, 10, 1_000m, effect);

    private static TradeDecisionMade Decision(OrderIntent intent) => new(Guid.NewGuid(), intent, "テスト判断", Now);

    private static (OrderScreeningService Service, InMemoryInformationDegradationStore Degradation) Create()
    {
        var degradation = new InMemoryInformationDegradationStore();
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
        var store = new InMemoryInformationDegradationStore();

        store.BlocksNewEntries.Should().BeFalse();
        store.MarkRecovered("unknown"); // 冪等（未登録の回復は無視）
        store.BlocksNewEntries.Should().BeFalse();

        store.MarkDegraded("news");
        store.MarkDegraded("news"); // 重複登録も冪等
        store.BlocksNewEntries.Should().BeTrue();

        store.MarkRecovered("news");
        store.BlocksNewEntries.Should().BeFalse();
    }
}
