using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Infrastructure.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace AiStockTrading.RiskManagement.Infrastructure.Tests;

// #270, FR-10, FR-05, IADR-0113: moomoo 経路（非同期約定）でも統制上限が paper と同等に実効することの回帰。
//
// 事象（#270 実測）: moomoo は発注時に Accepted（約定 0）を返し、約定を追跡する経路が無かったため trade_fills が
// 0 行のまま＝「まだ何も取引していない」状態となり、5 分ごとの判断サイクルのたびに新規発注が積み上がった
// （dailyOrderRemaining が基準資金相当のまま減らない）。paper は即時 Filled のため露呈しない。
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

    // FR-19, #332, IADR-0132 決定5: 差金決済防止ガードの適用対象は**日本株の現物**である
    // （米国株は信用口座で運用するため Good Faith Violation が発生しない）。本回帰は同ガードが
    // 約定到達後に拘束することを見るため、適用対象である日本株現物の注文を用いる。
    // #364, IADR-0152 決定1: 基準通貨は USD であり日本株は非基準通貨のため、換算レート（USD per JPY）を
    // 同伴させる。丸いテスト用レート 0.01 で 1 株 ¥1,000 ＝ $10、10 株で $100（equity $3,000 の統制上限内）。
    private const decimal JpyToUsd = 0.01m;

    private static OrderIntent Entry() =>
        new("7203", Market.Japan, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
            10, 1_000m, PositionEffect.Open, StopLossPrice: null, FxRateToBase: JpyToUsd);

    // ADR-0013, IADR-0129, #354: MassTransit のテストハーネスから Wolverine.Tracking へ移行した。
    // 明示登録（AddConsumer<T>）は「規約発見を止めて対象型だけを含める」形へ写す
    // （テストの対象範囲を旧テストと同一に保つ）。実ブローカへは接続しない。
    private static Task<IHost> BuildHostAsync(InMemoryPortfolioLedgerStore ledger) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IPortfolioLedgerStore>(ledger);
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<OrderApprovedLedgerHandler>()
                    .IncludeType<OrderExecutedLedgerHandler>();
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    // 台帳 → 射影 → スナップショット → スクリーニング／サイジング文脈（本番と同じ組み立て）。
    private static (OrderScreeningService Screening, SizingContextService Sizing) BuildRiskChain(
        InMemoryPortfolioLedgerStore ledger)
    {
        var clock = new FixedClock();
        var provider = new LedgerPortfolioStateProvider(ledger, clock);
        // #375, IADR-0153 決定2: 本テストの注文意図は内蔵 paper であり口座種別を要求しない。
        // 観測ストアは空のまま（＝口座種別を確認できていない）を明示的に渡す。
        var snapshotBuilder = new PortfolioSnapshotBuilder(
            provider, new InMemoryKillSwitchStore(), new InMemoryPauseStore(),
            new InMemoryBrokerAccountObservationStore(TimeProvider.System));
        var settings = new InMemoryRiskSettingsStore();
        return (new OrderScreeningService(settings, snapshotBuilder, new InMemoryLockoutStore(), clock,
                new WeekendBusinessCalendar()),
            new SizingContextService(snapshotBuilder, settings));
    }

    [Fact]
    public async Task 約定が台帳へ届くまで統制は拘束せず届いた後は同日再エントリーを拒否する()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        using var host = await BuildHostAsync(ledger);
        var (screening, sizing) = BuildRiskChain(ledger);

        // 1 回目の判断は承認される（保有なし・当日取引なし）。
        var first = screening.Screen(new TradeDecisionMade(Guid.NewGuid(), Entry(), "1 回目", Now));
        first.IsApproved.Should().BeTrue();
        var decisionId = first.Approved!.DecisionId;
        var session1 = await host.TrackActivity().InvokeMessageAndWaitAsync(new OrderApproved(decisionId, Entry(), 10, Now));
        session1.Executed.MessagesOf<OrderApproved>().Should().NotBeEmpty();

        var dailyRemainingBefore = sizing.Build().DailyOrderRemaining;

        // moomoo の発注応答（Accepted・約定 0）。この時点では台帳に何も載らない＝#270 の事象そのもの。
        var session2 = await host.TrackActivity().InvokeMessageAndWaitAsync(new OrderExecuted(decisionId, "ORD-1", OrderStatus.Accepted, 0, 0m, Now, BrokerProvider.MoomooSimulate));
        session2.Executed.MessagesOf<OrderExecuted>().Should().NotBeEmpty();

        ledger.GetFills().Should().BeEmpty();
        sizing.Build().DailyOrderRemaining.Should().Be(dailyRemainingBefore, "未約定は発注枠を消費しない");
        screening.Screen(new TradeDecisionMade(Guid.NewGuid(), Entry(), "未約定中", Now))
            .IsApproved.Should().BeTrue("約定が無い間は同日再エントリーの統制が拘束しない（追跡が必要な理由）");

        // 追跡ポーラーが終端化を観測して再発行した約定。ここで統制の入力が満たされる。
        var session3 = await host.TrackActivity().InvokeMessageAndWaitAsync(new OrderExecuted(decisionId, "ORD-1", OrderStatus.Filled, 10, 1_000m, Now, BrokerProvider.MoomooSimulate));
        session3.Executed.MessagesOf<OrderExecuted>()
            .Should().Contain(m => m.Status == OrderStatus.Filled);

        ledger.GetFills().Should().ContainSingle().Which.Quantity.Should().Be(10);

        // FR-10: 同日再エントリーの禁止が効く（paper 経路と同一の挙動）。
        var reentry = screening.Screen(new TradeDecisionMade(Guid.NewGuid(), Entry(), "同日再エントリー", Now));
        reentry.IsApproved.Should().BeFalse();
        reentry.Rejected!.Reasons.Should().Contain(RejectionReason.SameDayReentry);

        // FR-10: 日次発注上限・段階資金上限の残枠も約定額（10 株 × ¥1,000 × 0.01 ＝ $100）だけ減る。
        var context = sizing.Build();
        context.DailyOrderRemaining.Should().Be(dailyRemainingBefore - 100m);
        (dailyRemainingBefore - context.DailyOrderRemaining).Should().Be(100m);

        await host.StopAsync();
    }

    [Fact]
    public async Task 部分約定でも統制の入力は約定分だけ進む()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        using var host = await BuildHostAsync(ledger);
        var (_, sizing) = BuildRiskChain(ledger);

        var decisionId = Guid.NewGuid();
        var session1 = await host.TrackActivity().InvokeMessageAndWaitAsync(new OrderApproved(decisionId, Entry(), 10, Now));
        session1.Executed.MessagesOf<OrderApproved>().Should().NotBeEmpty();
        var before = sizing.Build();

        var session2 = await host.TrackActivity().InvokeMessageAndWaitAsync(new OrderExecuted(decisionId, "ORD-1", OrderStatus.PartiallyFilled, 4, 1_000m, Now, BrokerProvider.MoomooSimulate));
        session2.Executed.MessagesOf<OrderExecuted>().Should().NotBeEmpty();

        // 部分約定（4 株 × ¥1,000 × 0.01 ＝ $40）分だけ枠が減る。全量約定を待たない（待つ間は統制が素通しになる）。
        var partial = sizing.Build();
        (before.DailyOrderRemaining - partial.DailyOrderRemaining).Should().Be(40m);
        (before.StageCapitalRemaining - partial.StageCapitalRemaining).Should().Be(40m);

        // 累積 10 株で終端化 → 差分ではなく累積で置き換わる（二重計上しない）。
        var session3 = await host.TrackActivity().InvokeMessageAndWaitAsync(new OrderExecuted(decisionId, "ORD-1", OrderStatus.Filled, 10, 1_000m, Now, BrokerProvider.MoomooSimulate));
        session3.Executed.MessagesOf<OrderExecuted>()
            .Should().Contain(m => m.Status == OrderStatus.Filled);

        var full = sizing.Build();
        (before.DailyOrderRemaining - full.DailyOrderRemaining).Should().Be(100m);
        (before.StageCapitalRemaining - full.StageCapitalRemaining).Should().Be(100m);

        await host.StopAsync();
    }
}
