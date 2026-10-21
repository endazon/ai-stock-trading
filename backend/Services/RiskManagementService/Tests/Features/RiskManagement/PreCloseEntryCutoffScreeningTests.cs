using RiskManagementService.Common.Abstractions;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.ExternalServices;
using RiskManagementService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace RiskManagementService.Tests;

// T-10-2923〜T-10-2925, FR-10, FR-19, #1396, IADR-0561 決定 2: 審査のサービスが閉場前の新規建ての締め切りを**市場の暦**（共有カーネルの MarketHours。
// 休場日・半日取引日の規則計算）と**市場ごとの構成**で供給すること。締め切りの前に承認した予約を締め切りの後に組み直して発行しないこと。
// 構成の読み取り（既定・範囲外・未知の市場）を固定する。
public class PreCloseEntryCutoffScreeningTests
{
    private static PortfolioState HealthyState => new()
    {
        LedgerEquity = 100_000m,
        OpenPositionCount = 0,
        InvestedCapital = 0m,
        DailyOrderedAmount = 0m,
        DailyRealizedPnl = 0m,
        UnrealizedPnl = 0m,
    };

    private static OrderIntent Intent(Market market, PositionEffect effect = PositionEffect.Open) =>
        new(market == Market.Japan ? "7203" : "MARA", market, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
            10, market == Market.Japan ? 30m : 20m, effect);

    private static (OrderScreeningService Service, FakeClock Clock, InMemoryEntryReservationStore Reservations) Create(
        DateTimeOffset now, PreCloseEntryCutoffSettings? cutoff = null)
    {
        var clock = new FakeClock(now, DateOnly.FromDateTime(now.UtcDateTime));
        var builder = new PortfolioSnapshotBuilder(
            new FakePortfolioStateProvider(HealthyState), new InMemoryKillSwitchStore(), new InMemoryPauseStore(),
            FakeBrokerAccountObservations.NotObserved(), FakeInformationDegradation.Affirmed(),
            capitalBaseline: FakeCapitalBaseline.Of(100_000m));
        var reservations = new InMemoryEntryReservationStore();
        var service = new OrderScreeningService(
            new InMemoryRiskSettingsStore(), builder, new InMemoryLockoutStore(), clock, new WeekendBusinessCalendar(),
            new InMemoryBuyInInferenceStore(), new InMemoryPortfolioLedgerStore(), TestShortSellContexts.Unavailable(clock),
            reservations, cutoff is null ? ConfiguredSessionAndThemeControls.Default : ConfiguredSessionAndThemeControls.Default with { PreCloseEntryCutoff = cutoff });
        return (service, clock, reservations);
    }

    private static Task<ScreeningOutcome> Screen(OrderScreeningService service, OrderIntent intent, DateTimeOffset at) =>
        service.ScreenAsync(new TradeDecisionMade(Guid.NewGuid(), intent, "定時判断", at), TestContext.Current.CancellationToken);

    // 通常の取引日（2026-10-19 月・夏時間）: 閉場 16:00 ET＝20:00 UTC。締め切り 15:00 ET＝19:00 UTC。
    [Theory]
    [InlineData(18, 59, true)]
    [InlineData(19, 0, false)]
    [InlineData(19, 58, false)]
    public async Task T_10_2923_米国の通常の取引日は16時ETの60分前から新規建てを拒否する(int hour, int minute, bool approved)
    {
        var now = new DateTimeOffset(2026, 10, 19, hour, minute, 0, TimeSpan.Zero);
        var (service, _, _) = Create(now);

        var outcome = await Screen(service, Intent(Market.UnitedStates), now);

        outcome.IsApproved.Should().Be(approved);
        if (!approved)
            outcome.Rejected!.Reasons.Should().Equal(RejectionReason.PreCloseEntryCutoff);
    }

    // 半日取引日（2026-11-27 金・感謝祭の翌日・冬時間）: 閉場 13:00 ET＝18:00 UTC。締め切り 12:00 ET＝17:00 UTC（16:00 を基準にしない）。
    [Theory]
    [InlineData(16, 59, true)]
    [InlineData(17, 0, false)]
    public async Task T_10_2923_半日取引日は前倒しの閉場13時ETの60分前から拒否する(int hour, int minute, bool approved)
    {
        var now = new DateTimeOffset(2026, 11, 27, hour, minute, 0, TimeSpan.Zero);
        var (service, _, _) = Create(now);

        (await Screen(service, Intent(Market.UnitedStates), now)).IsApproved.Should().Be(approved);
    }

    // 休場日（2026-11-26 感謝祭）: 閉場が無いので締め切りの理由は立たない（休場日の扱いは場中の判定の側）。
    [Fact]
    public async Task T_10_2923_休場日は締め切りの理由を立てない()
    {
        var now = new DateTimeOffset(2026, 11, 26, 20, 30, 0, TimeSpan.Zero); // 15:30 ET
        var (service, _, _) = Create(now);

        var outcome = await Screen(service, Intent(Market.UnitedStates), now);

        (outcome.Rejected?.Reasons ?? []).Should().NotContain(RejectionReason.PreCloseEntryCutoff);
    }

    // 東証（2026-10-20 火）: 大引け 15:30 JST＝06:30 UTC。締め切り 14:30 JST＝05:30 UTC。昼休み（11:30）は閉場ではない。
    [Theory]
    [InlineData(2, 25, true)]  // 11:25 JST（前場の引け前）: 締め切りではない
    [InlineData(5, 29, true)]
    [InlineData(5, 30, false)]
    public async Task T_10_2923_東証は大引け15時30分JSTの60分前から拒否する(int hour, int minute, bool approved)
    {
        var now = new DateTimeOffset(2026, 10, 20, hour, minute, 0, TimeSpan.Zero);
        var (service, _, _) = Create(now);

        (await Screen(service, Intent(Market.Japan), now)).IsApproved.Should().Be(approved);
    }

    [Fact]
    public async Task T_10_2923_分数は市場ごとの構成を使う()
    {
        var cutoff = new PreCloseEntryCutoffSettings(new Dictionary<Market, int>
        {
            [Market.UnitedStates] = 30,
            [Market.Japan] = 60,
        });
        var now = new DateTimeOffset(2026, 10, 19, 19, 15, 0, TimeSpan.Zero); // 閉場の 45 分前
        var (service, _, _) = Create(now, cutoff);

        (await Screen(service, Intent(Market.UnitedStates), now)).IsApproved.Should().BeTrue();
    }

    [Fact]
    public async Task T_10_2923_手仕舞いは締め切りの中でも通す()
    {
        var now = new DateTimeOffset(2026, 10, 19, 19, 50, 0, TimeSpan.Zero);
        var (service, _, _) = Create(now);

        var close = Intent(Market.UnitedStates, PositionEffect.Close) with { Side = TradeSide.Sell };
        (await Screen(service, close, now)).IsApproved.Should().BeTrue();
    }

    // 🔴 締め切りの前に承認した予約（承認の発行が届く前に再配送された）を、締め切りの後に組み直して発行しない。
    [Fact]
    public async Task T_10_2924_締め切りの前に承認した予約を締め切りの後に再発行しない()
    {
        var before = new DateTimeOffset(2026, 10, 19, 18, 50, 0, TimeSpan.Zero);
        var (service, clock, reservations) = Create(before);
        var decision = new TradeDecisionMade(Guid.NewGuid(), Intent(Market.UnitedStates), "定時判断", before);

        (await service.ScreenAsync(decision, TestContext.Current.CancellationToken)).IsApproved.Should().BeTrue();
        reservations.Find(decision.DecisionId).Should().NotBeNull();

        clock.UtcNow = new DateTimeOffset(2026, 10, 19, 19, 5, 0, TimeSpan.Zero);
        var redelivered = await service.ScreenAsync(decision, TestContext.Current.CancellationToken);

        redelivered.IsApproved.Should().BeFalse();
        redelivered.Rejected!.Reasons.Should().Equal(RejectionReason.PreCloseEntryCutoff);
        reservations.Find(decision.DecisionId)!.IsVoided.Should().BeTrue();
    }

    [Fact]
    public void T_10_2925_構成が無ければ両市場とも60分()
    {
        var settings = PreCloseEntryCutoffLoader.FromConfiguration(new ConfigurationBuilder().Build());

        settings.MinutesFor(Market.UnitedStates).Should().Be(60);
        settings.MinutesFor(Market.Japan).Should().Be(60);
    }

    [Fact]
    public void T_10_2925_市場ごとに構成で変えられる()
    {
        var settings = PreCloseEntryCutoffLoader.FromConfiguration(Config(("UnitedStates", "45")));

        settings.MinutesFor(Market.UnitedStates).Should().Be(45);
        settings.MinutesFor(Market.Japan).Should().Be(60);
    }

    // 🔴 統制を外す値（0・負）・場中の長さを超える値・読めない値・未知の市場は起動を止める（黙って既定へ倒さない）。
    [Theory]
    [InlineData("UnitedStates", "0")]
    [InlineData("UnitedStates", "-5")]
    [InlineData("UnitedStates", "391")]
    [InlineData("Japan", "331")]
    [InlineData("UnitedStates", "abc")]
    [InlineData("Unitedstates", "30")]
    public void T_10_2925_範囲外や読めない値は起動を止める(string market, string value)
    {
        var act = () => PreCloseEntryCutoffLoader.FromConfiguration(Config((market, value)));

        act.Should().Throw<InvalidOperationException>();
    }

    // 実時刻で動く試験の時計（OutsidePreCloseCutoffClock）が、どの時刻でも両市場の締め切りの外を返すこと（試験の結果を実行時刻に依らせない）。
    [Fact]
    public void T_10_2925_実時刻の試験の時計はどの時刻でも締め切りの外を返す()
    {
        var cutoff = TradingDefaults.CreatePreCloseEntryCutoff();
        foreach (var day in new[] { new DateTime(2026, 10, 19), new DateTime(2026, 11, 27), new DateTime(2026, 3, 9), new DateTime(2026, 11, 2) })
        {
            for (var minute = 0; minute < 24 * 60; minute += 5)
            {
                var resolved = OutsidePreCloseCutoffClock.Resolve(new DateTimeOffset(day.AddMinutes(minute), TimeSpan.Zero));
                foreach (var market in new[] { Market.UnitedStates, Market.Japan })
                {
                    new PreCloseEntryCutoffSupply(
                            resolved, AiStockTrading.Shared.Kernel.Trading.MarketHours.SessionCloseOn(market, resolved), cutoff.MinutesFor(market))
                        .IsWithinCutoff.Should().BeFalse($"{day:yyyy-MM-dd} +{minute} 分 → {resolved:O}（{market}）");
                }
            }
        }
    }

    private static IConfiguration Config(params (string Market, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e =>
                new KeyValuePair<string, string?>($"{PreCloseEntryCutoffLoader.Section}:{e.Market}", e.Value)))
            .Build();
}
