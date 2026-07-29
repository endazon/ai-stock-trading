using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Worker.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.RiskManagement.Worker.Tests;

// #270, FR-10, FR-05, IADR-0112: moomoo 経路（非同期約定）でも統制上限が paper と同等に実効することの回帰。
//
// 事象（#270 実測）: moomoo は発注時に Accepted（約定 0）を返し、約定を追跡する経路が無かったため trade_fills が
// 0 行のまま＝「まだ何も取引していない」状態となり、5 分ごとの判断サイクルのたびに新規発注が積み上がった
// （dailyOrderRemaining が ¥170,000,000 のまま減らない）。paper は即時 Filled のため露呈しない。
//
// 本テストは「約定が台帳へ届いた後に統制が拘束する」ことを、実 Consumer ＋ 実台帳 ＋ 実射影 ＋ 実スクリーニングの
// 通し（合成の要所を実物のまま）で固定する。
public class MoomooFillControlRegressionTests
{
    private static readonly DateOnly TradingDay = new(2026, 7, 29);
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 6, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;

        public DateOnly Today => TradingDay;
    }

    private static OrderIntent Entry() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper, 10, 1_000m);

    private static ServiceProvider BuildProvider(InMemoryPortfolioLedgerStore ledger) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IPortfolioLedgerStore>(ledger)
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<OrderApprovedLedgerConsumer>();
                x.AddConsumer<OrderExecutedLedgerConsumer>();
            })
            .BuildServiceProvider(true);

    // 台帳 → 射影 → スナップショット → スクリーニング／サイジング文脈（本番と同じ組み立て）。
    private static (OrderScreeningService Screening, SizingContextService Sizing) BuildRiskChain(
        InMemoryPortfolioLedgerStore ledger)
    {
        var clock = new FixedClock();
        var provider = new LedgerPortfolioStateProvider(ledger, clock);
        var snapshotBuilder = new PortfolioSnapshotBuilder(
            provider, new InMemoryKillSwitchStore(), new InMemoryPauseStore());
        var settings = new InMemoryRiskSettingsStore();
        return (new OrderScreeningService(settings, snapshotBuilder, new InMemoryLockoutStore(), clock,
                new WeekendBusinessCalendar()),
            new SizingContextService(snapshotBuilder, settings));
    }

    [Fact]
    public async Task 約定が台帳へ届くまで統制は拘束せず届いた後は同日再エントリーを拒否する()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        await using var provider = BuildProvider(ledger);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var (screening, sizing) = BuildRiskChain(ledger);

        // 1 回目の判断は承認される（保有なし・当日取引なし）。
        var first = screening.Screen(new TradeDecisionMade(Guid.NewGuid(), Entry(), "1 回目", Now));
        first.IsApproved.Should().BeTrue();
        var decisionId = first.Approved!.DecisionId;
        await harness.Bus.Publish(new OrderApproved(decisionId, Entry(), 10, Now));
        (await harness.Consumed.Any<OrderApproved>()).Should().BeTrue();

        var dailyRemainingBefore = sizing.Build().DailyOrderRemaining;

        // moomoo の発注応答（Accepted・約定 0）。この時点では台帳に何も載らない＝#270 の事象そのもの。
        await harness.Bus.Publish(new OrderExecuted(decisionId, "ORD-1", OrderStatus.Accepted, 0, 0m, Now));
        (await harness.Consumed.Any<OrderExecuted>()).Should().BeTrue();

        ledger.GetFills().Should().BeEmpty();
        sizing.Build().DailyOrderRemaining.Should().Be(dailyRemainingBefore, "未約定は発注枠を消費しない");
        screening.Screen(new TradeDecisionMade(Guid.NewGuid(), Entry(), "未約定中", Now))
            .IsApproved.Should().BeTrue("約定が無い間は同日再エントリーの統制が拘束しない（追跡が必要な理由）");

        // 追跡ポーラーが終端化を観測して再発行した約定。ここで統制の入力が満たされる。
        await harness.Bus.Publish(new OrderExecuted(decisionId, "ORD-1", OrderStatus.Filled, 10, 1_000m, Now));
        (await harness.Consumed.Any<OrderExecuted>(x => x.Context.Message.Status == OrderStatus.Filled))
            .Should().BeTrue();

        ledger.GetFills().Should().ContainSingle().Which.Quantity.Should().Be(10);

        // FR-10: 同日再エントリーの禁止が効く（paper 経路と同一の挙動）。
        var reentry = screening.Screen(new TradeDecisionMade(Guid.NewGuid(), Entry(), "同日再エントリー", Now));
        reentry.IsApproved.Should().BeFalse();
        reentry.Rejected!.Reasons.Should().Contain(RejectionReason.SameDayReentry);

        // FR-10: 日次発注上限・段階資金上限の残枠も約定額（10 株 × 1,000 円）だけ減る。
        var context = sizing.Build();
        context.DailyOrderRemaining.Should().Be(dailyRemainingBefore - 10_000m);
        (dailyRemainingBefore - context.DailyOrderRemaining).Should().Be(10_000m);

        await harness.Stop();
    }

    [Fact]
    public async Task 部分約定でも統制の入力は約定分だけ進む()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        await using var provider = BuildProvider(ledger);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var (_, sizing) = BuildRiskChain(ledger);

        var decisionId = Guid.NewGuid();
        await harness.Bus.Publish(new OrderApproved(decisionId, Entry(), 10, Now));
        (await harness.Consumed.Any<OrderApproved>()).Should().BeTrue();
        var before = sizing.Build();

        await harness.Bus.Publish(new OrderExecuted(decisionId, "ORD-1", OrderStatus.PartiallyFilled, 4, 1_000m, Now));
        (await harness.Consumed.Any<OrderExecuted>()).Should().BeTrue();

        // 部分約定（4 株 × 1,000 円）分だけ枠が減る。全量約定を待たない（待つ間は統制が素通しになる）。
        var partial = sizing.Build();
        (before.DailyOrderRemaining - partial.DailyOrderRemaining).Should().Be(4_000m);
        (before.StageCapitalRemaining - partial.StageCapitalRemaining).Should().Be(4_000m);

        // 累積 10 株で終端化 → 差分ではなく累積で置き換わる（二重計上しない）。
        await harness.Bus.Publish(new OrderExecuted(decisionId, "ORD-1", OrderStatus.Filled, 10, 1_000m, Now));
        (await harness.Consumed.Any<OrderExecuted>(x => x.Context.Message.Status == OrderStatus.Filled))
            .Should().BeTrue();

        var full = sizing.Build();
        (before.DailyOrderRemaining - full.DailyOrderRemaining).Should().Be(10_000m);
        (before.StageCapitalRemaining - full.StageCapitalRemaining).Should().Be(10_000m);

        await harness.Stop();
    }
}
