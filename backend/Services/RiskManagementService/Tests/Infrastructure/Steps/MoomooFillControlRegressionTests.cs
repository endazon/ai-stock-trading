using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Infrastructure.ExternalServices;
using RiskManagementService.Common.Abstractions;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.GetSizingContext;
using RiskManagementService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Tests;

// #270, FR-10, FR-05, IADR-0113: moomoo 経路（非同期約定）でも統制上限が paper と同等に実効することの回帰。
//
// 事象（#270 実測）: moomoo は発注時に Accepted（約定 0）を返し、約定を追跡する経路が無かったため trade_fills が
// 0 行のまま＝「まだ何も取引していない」状態となり、5 分ごとの判断サイクルのたびに新規発注が積み上がった
// （dailyOrderRemaining が基準資金相当のまま減らない）。paper は即時 Filled のため露呈しない。
//
// #829, IADR-0346: 約定を届けても、**届く前の間**（指値が溜まっている間）は素通しのままだった。計画 FR-10 は
// 日次枠を「新規建ての**発注代金**の合計」で定めるため、承認済みで未終端の新規建ても算入する。
//
// 本テストは実 Consumer（台帳・注文アクティビティ）＋ 実台帳 ＋ 実射影 ＋ 実スクリーニングの通し
// （合成の要所を実物のまま）で固定する。
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

    private static OrderIntent Entry(int quantity = 10) =>
        new("7203", Market.Japan, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
            quantity, 1_000m, PositionEffect.Open, StopLossPrice: null, FxRateToBase: JpyToUsd);

    private sealed record Stores(InMemoryPortfolioLedgerStore Ledger, InMemoryOrderActivityStore Activity);

    private static Stores NewStores() => new(new InMemoryPortfolioLedgerStore(), new InMemoryOrderActivityStore());

    // ADR-0013, IADR-0129, #354: MassTransit のテストハーネスから Wolverine.Tracking へ移行した。
    // 明示登録（AddConsumer<T>）は「規約発見を止めて対象型だけを含める」形へ写す
    // （テストの対象範囲を旧テストと同一に保つ）。実ブローカへは接続しない。
    // #829, IADR-0346: 注文アクティビティの射影ハンドラ（本番では同じハンドラチェーンで台帳と並走する）を含める。
    private static Task<IHost> BuildHostAsync(Stores stores) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IPortfolioLedgerStore>(stores.Ledger);
                opts.Services.AddSingleton<IOrderActivityStore>(stores.Activity);
                // #611, IADR-0286: 承認ハンドラが要する認識時レートの解決器（本テストの関心外・未記録で通す）。
                opts.Services.AddSingleton<IRecognitionFxRateResolver>(new StubRecognitionFxRateResolver());
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<OrderApprovedLedgerHandler>()
                    .IncludeType<OrderExecutedLedgerHandler>()
                    .IncludeType<OrderApprovedActivityHandler>()
                    .IncludeType<OrderExecutedActivityHandler>()
                    .IncludeType<OrderDispatchForgoneActivityHandler>();
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    // 台帳 → 射影 → スナップショット → スクリーニング／サイジング文脈（本番と同じ組み立て）。
    private static (OrderScreeningService Screening, SizingContextService Sizing) BuildRiskChain(
        Stores stores, RiskManagementSettings? settings = null)
    {
        var clock = new FixedClock();
        var provider = new LedgerPortfolioStateProvider(
            stores.Ledger, new InMemoryWorkingEntryOrderSource(stores.Ledger, stores.Activity), clock);
        // #375, IADR-0153 決定2: 本テストの注文意図は内蔵 paper であり口座種別を要求しない。
        // 観測ストアは空のまま（＝口座種別を確認できていない）を明示的に渡す。
        var snapshotBuilder = new PortfolioSnapshotBuilder(
            provider, new InMemoryKillSwitchStore(), new InMemoryPauseStore(),
            new InMemoryBrokerAccountObservationStore(TimeProvider.System),
            FakeInformationDegradation.Affirmed(), capitalBaseline: FakeCapitalBaseline.Of(TradingDefaults.InitialCapital));
        var settingsStore = new InMemoryRiskSettingsStore(settings);
        // #428: 推定台帳は必須依存。本テストは強制買戻しを関心に持たないため空の台帳を渡す。
        return (new OrderScreeningService(settingsStore, snapshotBuilder, new InMemoryLockoutStore(), clock,
                new WeekendBusinessCalendar(), new InMemoryBuyInInferenceStore()),
            new SizingContextService(snapshotBuilder, settingsStore));
    }

    private static async Task ApproveAsync(IHost host, Guid decisionId, OrderIntent intent)
    {
        var session = await host.TrackActivityForTest()
            .InvokeMessageAndWaitAsync(new OrderApproved(decisionId, intent, intent.Quantity, Now));
        session.Executed.MessagesOf<OrderApproved>().Should().NotBeEmpty();
    }

    private static async Task ExecutedAsync(IHost host, Guid decisionId, OrderStatus status, int filled)
    {
        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new OrderExecuted(decisionId, "ORD-" + decisionId, status, filled, filled == 0 ? 0m : 1_000m, Now,
                BrokerProvider.MoomooSimulate));
        session.Executed.MessagesOf<OrderExecuted>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task 未約定の間も発注代金が枠を消費し約定が届いても二重に数えず同日再エントリーは約定後に拒否する()
    {
        var stores = NewStores();
        using var host = await BuildHostAsync(stores);
        var (screening, sizing) = BuildRiskChain(stores);

        var dailyRemainingBefore = sizing.Build().DailyOrderRemaining;

        // 1 回目の判断は承認される（保有なし・当日取引なし）。
        var first = screening.Screen(new TradeDecisionMade(Guid.NewGuid(), Entry(), "1 回目", Now));
        first.IsApproved.Should().BeTrue();
        var decisionId = first.Approved!.DecisionId;
        await ApproveAsync(host, decisionId, Entry());

        // moomoo の発注応答（Accepted・約定 0）。台帳の約定は無い＝#270 の事象の形。
        await ExecutedAsync(host, decisionId, OrderStatus.Accepted, 0);
        stores.Ledger.GetFills().Should().BeEmpty();

        // FR-10, #829, IADR-0346: 約定を待たず、発注代金（10 株 × ¥1,000 × 0.01 ＝ $100）が枠を消費する。
        sizing.Build().DailyOrderRemaining.Should().Be(dailyRemainingBefore - 100m, "未約定でも発注代金は枠を消費する");
        // IADR-0346 決定4: 同日再エントリーの入力は約定だけ（決済は約定でしか成立しない）。
        screening.Screen(new TradeDecisionMade(Guid.NewGuid(), Entry(), "未約定中", Now))
            .IsApproved.Should().BeTrue("未約定の新規建ては同日再エントリーの入力に算入しない");

        // 追跡ポーラーが終端化を観測して再発行した約定。
        await ExecutedAsync(host, decisionId, OrderStatus.Filled, 10);
        stores.Ledger.GetFills().Should().ContainSingle().Which.Quantity.Should().Be(10);

        // FR-10: 同日再エントリーの禁止が効く（paper 経路と同一の挙動）。
        var reentry = screening.Screen(new TradeDecisionMade(Guid.NewGuid(), Entry(), "同日再エントリー", Now));
        reentry.IsApproved.Should().BeFalse();
        reentry.Rejected!.Reasons.Should().Contain(RejectionReason.SameDayReentry);

        // 約定へ置き換わっても合計は $100 のまま（未約定分と約定分を二重に数えない）。
        sizing.Build().DailyOrderRemaining.Should().Be(dailyRemainingBefore - 100m);

        await host.StopAsync();
    }

    [Fact]
    public async Task 部分約定でも約定分と残数量の合計は発注代金のまま変わらない()
    {
        var stores = NewStores();
        using var host = await BuildHostAsync(stores);
        var (_, sizing) = BuildRiskChain(stores);

        var before = sizing.Build();
        var decisionId = Guid.NewGuid();
        await ApproveAsync(host, decisionId, Entry());

        // 承認直後: 発注代金 $100 が日次枠・段階資金枠を消費する。
        var approved = sizing.Build();
        (before.DailyOrderRemaining - approved.DailyOrderRemaining).Should().Be(100m);
        (before.StageCapitalRemaining - approved.StageCapitalRemaining).Should().Be(100m);

        // 部分約定（4 株）: 約定 $40 ＋ 残 6 株 $60 ＝ $100。
        await ExecutedAsync(host, decisionId, OrderStatus.PartiallyFilled, 4);
        var partial = sizing.Build();
        (before.DailyOrderRemaining - partial.DailyOrderRemaining).Should().Be(100m);
        (before.StageCapitalRemaining - partial.StageCapitalRemaining).Should().Be(100m);

        // 累積 10 株で終端化 → 差分ではなく累積で置き換わる（二重計上しない）。
        await ExecutedAsync(host, decisionId, OrderStatus.Filled, 10);
        var full = sizing.Build();
        (before.DailyOrderRemaining - full.DailyOrderRemaining).Should().Be(100m);
        (before.StageCapitalRemaining - full.StageCapitalRemaining).Should().Be(100m);

        await host.StopAsync();
    }

    // ── #829, IADR-0346: 未約定の承認済み新規建てが日次枠を拘束する ──
    //
    // 日次枠を equity の 50%（$1,500）に絞り、1 件 $700（70 株 × ¥1,000 × 0.01）で段階資金（Stage 0 ＝ 総資金 $3,000）・
    // 1 注文上限（25% ＝ $750）に掛からない形にする。未約定 2 件（$1,400）＋ 3 件目（$700）＝ $2,100 > $1,500。
    private static RiskManagementSettings TightDailyCap()
    {
        var defaults = TradingDefaults.CreateSettings();
        return defaults with { Limits = defaults.Limits with { MaxDailyOrderAmountRatio = 0.5m } };
    }

    private static async Task<(OrderScreeningService Screening, Guid First, Guid Second)> TwoWorkingEntriesAsync(
        IHost host, Stores stores)
    {
        var (screening, _) = BuildRiskChain(stores, TightDailyCap());
        var ids = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            var outcome = screening.Screen(new TradeDecisionMade(Guid.NewGuid(), Entry(70), $"{i + 1} 件目", Now));
            outcome.IsApproved.Should().BeTrue();
            var decisionId = outcome.Approved!.DecisionId;
            await ApproveAsync(host, decisionId, Entry(70));
            await ExecutedAsync(host, decisionId, OrderStatus.Accepted, 0);
            ids.Add(decisionId);
        }

        return (screening, ids[0], ids[1]);
    }

    // T-10-333: #829 の実測（指値が溜まる間に承認が続いた）の再現。
    [Fact]
    public async Task 未約定の承認済み新規建てが日次枠を消費し上限を超える3件目は拒否される()
    {
        var stores = NewStores();
        using var host = await BuildHostAsync(stores);
        var (screening, _, _) = await TwoWorkingEntriesAsync(host, stores);

        stores.Ledger.GetFills().Should().BeEmpty("約定は 1 件も無い（未約定の発注代金だけで拘束する）");

        var third = screening.Screen(new TradeDecisionMade(Guid.NewGuid(), Entry(70), "3 件目", Now));

        third.IsApproved.Should().BeFalse();
        third.Rejected!.Reasons.Should().Contain(RejectionReason.DailyOrderAmountExceeded);
        third.Rejected!.Reasons.Should().NotContain(RejectionReason.StageCapitalCapExceeded,
            "本シナリオは日次枠だけが効く形に組んである（段階資金 $3,000 には届かない）");

        await host.StopAsync();
    }

    // T-10-335: 取消された発注で枠を消費しない（実測 2026-09-17: 取消 10 件は承認から 30 秒以内に終端化）。
    [Fact]
    public async Task 約定ゼロで取り消された新規建ては枠を返す()
    {
        var stores = NewStores();
        using var host = await BuildHostAsync(stores);
        var (screening, first, _) = await TwoWorkingEntriesAsync(host, stores);

        await ExecutedAsync(host, first, OrderStatus.Cancelled, 0);

        screening.Screen(new TradeDecisionMade(Guid.NewGuid(), Entry(70), "取消後の 3 件目", Now))
            .IsApproved.Should().BeTrue("取消で約定しなかった発注代金は枠へ戻る");

        await host.StopAsync();
    }

    // T-10-338: 発注されなかった承認（OpenD 不達などの見送り・IADR-0211）で当日枠が枯れない。
    [Fact]
    public async Task 見送られた新規建ては枠を返す()
    {
        var stores = NewStores();
        using var host = await BuildHostAsync(stores);
        var (screening, _, second) = await TwoWorkingEntriesAsync(host, stores);

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new OrderDispatchForgone(second, Entry(70), OrderDispatchForgoneReason.BrokerUnavailable, Now));
        session.Executed.MessagesOf<OrderDispatchForgone>().Should().NotBeEmpty();

        screening.Screen(new TradeDecisionMade(Guid.NewGuid(), Entry(70), "見送り後の 3 件目", Now))
            .IsApproved.Should().BeTrue("発注されなかった承認は枠を消費しない");

        await host.StopAsync();
    }
}
