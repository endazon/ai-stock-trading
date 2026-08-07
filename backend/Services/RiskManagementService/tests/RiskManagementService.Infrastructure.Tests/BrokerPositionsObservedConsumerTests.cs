using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Infrastructure.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace AiStockTrading.RiskManagement.Infrastructure.Tests;

// FR-05, FR-10, FR-11, #292, IADR-0118: ブローカ建玉の観測を購読し、取引台帳との乖離を報告するハンドラ。
//
// ADR-0013, IADR-0129, #354: MassTransit のテストハーネスから Wolverine.Tracking へ移行した。
// 連続観測を要求するテストは「1 回目を流す → 2 回目を流す」の順序が本質であり、Wolverine では
// InvokeMessageAndWaitAsync が同期的に順序づくため、旧テストの `await harness.Consumed.Any<T>()`
// （2 回目の前に 1 回目の消費完了を待つ）は不要になった（待ち合わせの意味は保たれる）。
public class BrokerPositionsObservedConsumerTests
{
    private const string ServiceName = "ai-stock-trading.risk-management-service";

    private static readonly DateTimeOffset At = new(2026, 7, 30, 6, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => At;

        public DateOnly Today => DateOnly.FromDateTime(At.UtcDateTime);
    }

    // #305, IADR-0124: 追跡状態は共有ストア経由になった。ここでは 1 レプリカ相当のインメモリストアを与える。
    private static PositionDriftTracker NewTracker() =>
        new(new InMemoryPositionDriftStateStore(), NullLogger<PositionDriftTracker>.Instance);

    private static Task<IHost> BuildHostAsync(
        InMemoryPortfolioLedgerStore ledger,
        PositionDriftTracker tracker,
        IBuyInInferenceStore? buyInStore = null) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IPortfolioLedgerStore>(ledger);
                opts.Services.AddSingleton(tracker);
                opts.Services.AddSingleton<IClock, FixedClock>();
                // #419, IADR-0159: 同じ観測から強制買戻しを事後推定する（ハンドラの必須依存）。
                opts.Services.AddSingleton<IRiskSettingsStore>(new InMemoryRiskSettingsStore());
                opts.Services.AddSingleton(buyInStore ?? new InMemoryBuyInInferenceStore());
                opts.Services.AddSingleton(sp => new BuyInInferenceService(
                    sp.GetRequiredService<IPortfolioLedgerStore>(),
                    sp.GetRequiredService<IBuyInInferenceStore>(),
                    sp.GetRequiredService<IRiskSettingsStore>(),
                    sp.GetRequiredService<IClock>()));
                // 本番と同じ配線（キュー名・fan-out・再試行・DLQ）を用い、送信先だけ stub へ倒す。
                // ルーティングを入れないと発行先が 1 つも無く、送信そのものが起きない。
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<BrokerPositionsObservedHandler>();
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    // AAPL を 100 株保有している台帳。
    private static InMemoryPortfolioLedgerStore LedgerWithAapl(int quantity)
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var decisionId = Guid.NewGuid();
        ledger.AppendApproval(
            decisionId,
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
                quantity, 20m, PositionEffect.Open, StopLossPrice: null, FxRateToBase: 150m),
            At.AddDays(-1));
        ledger.AppendFill(decisionId, "open-1", quantity, 20m, At.AddDays(-1));
        return ledger;
    }

    private static BrokerPositionsObserved Observed(params BrokerPositionSnapshot[] positions) =>
        new(positions, At);

    [Fact]
    public async Task 一致していれば乖離を発行しない()
    {
        using var host = await BuildHostAsync(LedgerWithAapl(100), NewTracker());

        var session = await host.TrackActivityForTest()
            .InvokeMessageAndWaitAsync(Observed(new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 100, 20m)));
        session.Executed.MessagesOf<BrokerPositionsObserved>().Should().NotBeEmpty();

        session.Sent.MessagesOf<PositionReconciliationDrift>().Should().BeEmpty();

        await host.StopAsync();
    }

    [Fact]
    public async Task 一度きりの乖離では発行しない()
    {
        // 発注直後〜約定が台帳へ届くまでの一過性のズレで鳴らせない。
        using var host = await BuildHostAsync(LedgerWithAapl(100), NewTracker());

        var session = await host.TrackActivityForTest()
            .InvokeMessageAndWaitAsync(Observed(new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 80, 20m)));
        session.Executed.MessagesOf<BrokerPositionsObserved>().Should().NotBeEmpty();

        session.Sent.MessagesOf<PositionReconciliationDrift>().Should().BeEmpty();

        await host.StopAsync();
    }

    [Fact]
    public async Task 連続で同一の乖離なら双方の数量つきで発行する()
    {
        using var host = await BuildHostAsync(LedgerWithAapl(100), NewTracker());

        var observed = Observed(new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 80, 20m));
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(observed);
        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(observed);

        session.Sent.MessagesOf<PositionReconciliationDrift>().Should().Contain(m =>
            m.Drifts.Count == 1
            && m.Drifts[0].Symbol == "AAPL"
            && m.Drifts[0].LedgerQuantity == 100
            && m.Drifts[0].BrokerQuantity == 80
            && m.Drifts[0].Kind == PositionDriftKind.QuantityMismatch
            && m.ObservedAt == At
            && m.DetectedAt == At);

        await host.StopAsync();
    }

    [Fact]
    public async Task 台帳が空でもブローカにだけある建玉を検出する()
    {
        // 手動売買・外部約定。#270 破損期の過大建玉もこの形で現れる。
        using var host = await BuildHostAsync(new InMemoryPortfolioLedgerStore(), NewTracker());

        var observed = Observed(new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 4072, 20.5m));
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(observed);
        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(observed);

        session.Sent.MessagesOf<PositionReconciliationDrift>().Should().Contain(m =>
            m.Drifts[0].Kind == PositionDriftKind.BrokerOnly
            && m.Drifts[0].BrokerQuantity == 4072);

        await host.StopAsync();
    }

    [Fact]
    public async Task 建玉ゼロの観測で台帳側の建玉を乖離として検出する()
    {
        using var host = await BuildHostAsync(LedgerWithAapl(100), NewTracker());

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Observed());
        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Observed());

        session.Sent.MessagesOf<PositionReconciliationDrift>().Should().Contain(m =>
            m.Drifts[0].Kind == PositionDriftKind.LedgerOnly
            && m.Drifts[0].LedgerQuantity == 100);

        await host.StopAsync();
    }

    [Fact]
    public async Task 是正の発注は行わない()
    {
        // 乖離に対して自律的に発注する経路を作らない（IADR-0118）。承認イベントが 1 件も出ないことを固定する。
        using var host = await BuildHostAsync(LedgerWithAapl(100), NewTracker());

        var observed = Observed(new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 80, 20m));
        var first = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(observed);
        var second = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(observed);
        second.Sent.MessagesOf<PositionReconciliationDrift>().Should().NotBeEmpty();

        // 1 回目・2 回目のどちらでも是正の発注は出ない（旧テストはハーネス全体で見ていたため両方を見る）。
        first.Sent.MessagesOf<OrderApproved>().Should().BeEmpty();
        second.Sent.MessagesOf<OrderApproved>().Should().BeEmpty();
        first.Sent.MessagesOf<PositionCloseRequested>().Should().BeEmpty();
        second.Sent.MessagesOf<PositionCloseRequested>().Should().BeEmpty();

        await host.StopAsync();
    }

    // T-10-246: FR-10, FR-11, ADR-0016 決定4（2026-08-06 改訂）, #419, IADR-0159 ——
    // **同じ観測から強制買戻しを事後推定し、イベントとして発行する。**
    // 推定は乖離報告の連続観測条件に従わない（処理中の決済承認を差し引いて未反映そのものを説明に使うため、
    // 待つ必要が無い。推定が遅れることは決定4 の目的を損なう側の誤りである）。
    [Fact]
    public async Task 説明できない空売り建玉の消失から強制買戻しを推定して発行する()
    {
        var store = new InMemoryBuyInInferenceStore();
        using var host = await BuildHostAsync(LedgerWithShortGme(100), NewTracker(), store);

        // ブローカ側に当該建玉が無い＝自らの決済指示で説明できない消失。
        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Observed());

        session.Sent.MessagesOf<BuyInInferred>().Should().Contain(m =>
            m.Symbol == "GME"
            && m.LedgerShortQuantity == 100
            && m.BrokerShortQuantity == 0
            && m.NewlyInferredQuantity == 100);
        store.GetBanUntil("GME", Market.UnitedStates).Should().NotBeNull();

        await host.StopAsync();
    }

    // T-10-247（**否定形**）: 自らの決済約定で説明できる消失からは推定を発行しない。
    [Fact]
    public async Task 自らの決済で説明できる消失からは推定を発行しない()
    {
        var store = new InMemoryBuyInInferenceStore();
        // 100 株の空売りを自分で全量買い戻した台帳（建玉なし）。
        using var host = await BuildHostAsync(LedgerWithShortGme(100, closedQuantity: 100), NewTracker(), store);

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Observed());

        session.Sent.MessagesOf<BuyInInferred>().Should().BeEmpty();
        store.GetBanUntil("GME", Market.UnitedStates).Should().BeNull();

        await host.StopAsync();
    }

    // GME を quantity 株だけ空売りし、そのうち closedQuantity 株を自分で買い戻した台帳。
    private static InMemoryPortfolioLedgerStore LedgerWithShortGme(int quantity, int closedQuantity = 0)
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var openId = Guid.NewGuid();
        ledger.AppendApproval(
            openId,
            new OrderIntent("GME", Market.UnitedStates, TradeSide.Sell, ProductType.ShortSell,
                BrokerProvider.MoomooSimulate, quantity, 30m, PositionEffect.Open, StopLossPrice: 33m),
            At.AddDays(-2));
        ledger.AppendFill(openId, "short-open-1", quantity, 30m, At.AddDays(-2));

        if (closedQuantity > 0)
        {
            var closeId = Guid.NewGuid();
            ledger.AppendApproval(
                closeId,
                new OrderIntent("GME", Market.UnitedStates, TradeSide.Buy, ProductType.ShortSell,
                    BrokerProvider.MoomooSimulate, closedQuantity, 30m, PositionEffect.Close),
                At.AddDays(-1));
            ledger.AppendFill(closeId, "short-close-1", closedQuantity, 30m, At.AddDays(-1));
        }

        return ledger;
    }
}
