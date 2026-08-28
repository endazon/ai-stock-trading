using RiskManagementService.Application.Adapters;
using RiskManagementService.Application.Services;
using RiskManagementService.Application.State;
using RiskManagementService.Domain;
using RiskManagementService.Domain.Manipulation;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Application.Tests.Manipulation;

// FR-19, IADR-0006/0040: 検出器を注入した OrderScreeningService の結合検証。
// 受け入れ基準: ガード有効（既定）＋該当履歴 → 拒否（ManipulativeOrderPattern）／正常履歴 → 承認／ガード無効 → スキップ。
public class OrderScreeningManipulationTests
{
    private static readonly DateOnly TradingDay = new(2026, 7, 11);
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 9, 35, 0, TimeSpan.Zero);

    private static PortfolioState HealthyState => new()
    {
        Capital = 100_000m,
        OpenPositionCount = 0,
        InvestedCapital = 0m,
        DailyOrderedAmount = 0m,
        DailyRealizedPnl = 0m,
        UnrealizedPnl = 0m,
    };

    private static OrderIntent EntryIntent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 1_000m);

    private static TradeDecisionMade Decision(OrderIntent intent) =>
        new(Guid.NewGuid(), intent, "テスト判断", Now);

    // 見せ玉パターンを AAPL/US の窓内に投入する。
    private static void SeedManipulativeActivity(InMemoryOrderActivitySource source)
    {
        for (var i = 0; i < 6; i++)
        {
            var placedAt = Now.AddSeconds(-60 + i);
            source.Record("AAPL", Market.UnitedStates, new OrderActivityRecord
            {
                PlacedAt = placedAt,
                Side = TradeSide.Buy,
                Quantity = 10,
                FilledQuantity = 0,
                Status = OrderStatus.Cancelled,
                TerminalAt = placedAt.AddSeconds(1),
            });
        }
    }

    private static OrderScreeningService CreateService(
        InMemoryOrderActivitySource source, InMemoryRiskSettingsStore settingsStore)
    {
        var clock = new FakeClock(Now, TradingDay);
        var portfolio = new FakePortfolioStateProvider(HealthyState);
        var killSwitch = new InMemoryKillSwitchStore();
        var lockout = new InMemoryLockoutStore();
        // 本テストの注文は内蔵 paper であり口座種別を要求しない（IADR-0153 決定2）。
        var builder = new PortfolioSnapshotBuilder(
            portfolio, killSwitch, new InMemoryPauseStore(), FakeBrokerAccountObservations.NotObserved(),
            new InMemoryInformationDegradationStore());
        var detector = new ManipulativeOrderPatternDetector(
            source, clock, TradingDefaults.CreateManipulationDetectionSettings());
        // #428: 推定台帳は必須依存。本テストは強制買戻しを関心に持たないため**空の台帳**を渡す
        // （`null!` を渡すと必須化の意味が消える）。
        return new OrderScreeningService(
            settingsStore, builder, lockout, clock, new WeekendBusinessCalendar(),
            new InMemoryBuyInInferenceStore(), detector);
    }

    [Fact]
    public void ガード有効かつ該当履歴の注文は相場操縦で拒否される()
    {
        var source = new InMemoryOrderActivitySource();
        SeedManipulativeActivity(source);
        var service = CreateService(source, new InMemoryRiskSettingsStore());

        var outcome = service.Screen(Decision(EntryIntent()));

        outcome.IsApproved.Should().BeFalse();
        outcome.Rejected!.Reasons.Should().Contain(RejectionReason.ManipulativeOrderPattern);
    }

    [Fact]
    public void 該当履歴がなければ承認される()
    {
        // 検出器は注入されているが窓が空（該当なし）→ 相場操縦では拒否しない。
        var service = CreateService(new InMemoryOrderActivitySource(), new InMemoryRiskSettingsStore());

        var outcome = service.Screen(Decision(EntryIntent()));

        outcome.IsApproved.Should().BeTrue();
    }

    [Fact]
    public void ガード無効時は該当履歴でも相場操縦ではスキップする()
    {
        // ProhibitManipulativeOrderPatterns = false のとき検出器を呼ばない（IADR-0006）。
        var source = new InMemoryOrderActivitySource();
        SeedManipulativeActivity(source);
        var settingsStore = new InMemoryRiskSettingsStore();
        var current = settingsStore.GetCurrent();
        settingsStore.Save(current with
        {
            Guard = current.Guard with { ProhibitManipulativeOrderPatterns = false },
        });
        var service = CreateService(source, settingsStore);

        var outcome = service.Screen(Decision(EntryIntent()));

        outcome.Rejected?.Reasons.Should().NotContain(RejectionReason.ManipulativeOrderPattern);
        outcome.IsApproved.Should().BeTrue();
    }
}
