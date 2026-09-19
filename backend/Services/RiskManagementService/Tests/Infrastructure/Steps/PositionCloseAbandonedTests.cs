using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Tests;

// 🔴 FR-09, FR-10, FR-11, UC-06, #847, IADR-0357: **失効した手仕舞いを黙って捨てない。**
//
// 手仕舞いは当日注文であり、約定しなければ引け後に失効する。従来は `OrderExecuted(Status=Expired)` が
// 「約定 Expired 数量0@0」という一般的な Warning になるだけで、**それが手仕舞いだったことも、建玉が
// 残っていることも書かれていなかった**。無保護の建玉（S2）ではそのまま翌日へ持ち越す。
public class PositionCloseAbandonedTests
{
    private static readonly DateTimeOffset Approved = new(2026, 9, 18, 14, 13, 0, TimeSpan.Zero);

    private static OrderIntent CloseIntent(int qty) =>
        new("SOXL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
            qty, 334.09m, PositionEffect.Close);

    private static OrderIntent OpenIntent(int qty) =>
        new("SOXL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
            qty, 334.09m, PositionEffect.Open, StopLossPrice: 320m);

    private const string ServiceName = "ai-stock-trading.risk-management-service";

    // カスケード送信（ハンドラの戻り値）を session.Sent で捕捉するには**送信経路の登録**が要る。
    // 本ユニット共通の配線（規約ルーティング）を入れてから外部トランスポートを差し替える
    // ——OrderAmendmentDispatcherTests と同じ形。実ブローカへは接続しない。
    private static Task<IHost> BuildHostAsync(InMemoryPortfolioLedgerStore ledger) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IPortfolioLedgerStore>(ledger);
                opts.Services.AddSingleton<IRecognitionFxRateResolver>(new StubRecognitionFxRateResolver(150m));
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<OrderApprovedLedgerHandler>()
                    .IncludeType<OrderExecutedLedgerHandler>()
                    .IncludeType<OrderCancelledLedgerHandler>();
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static async Task<Guid> ApproveCloseAsync(IHost host, int quantity)
    {
        var decisionId = Guid.NewGuid();
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new OrderApproved(decisionId, CloseIntent(quantity), quantity, Approved));
        return decisionId;
    }

    // 🔴 #847（受け入れ基準 3）: 引け跨ぎで失効した手仕舞いが通知される。
    [Fact]
    public async Task 失効した手仕舞いは残った建玉つきで発行される()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        using var host = await BuildHostAsync(ledger);
        var decisionId = await ApproveCloseAsync(host, 3_381);

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderExecuted(
            decisionId, "1149564921959476304", OrderStatus.Expired, 0, 0m, Approved.AddHours(6),
            BrokerProvider.MoomooSimulate));

        var abandoned = session.Sent.MessagesOf<PositionCloseAbandoned>().Single();
        abandoned.DecisionId.Should().Be(decisionId);
        abandoned.Symbol.Should().Be("SOXL");
        abandoned.ApprovedQuantity.Should().Be(3_381);
        abandoned.FilledQuantity.Should().Be(0);
        abandoned.RemainingQuantity.Should().Be(3_381, "手仕舞えなかった株数が黙って翌日へ持ち越される");
        abandoned.TerminalStatus.Should().Be(OrderStatus.Expired);

        await host.StopAsync();
    }

    // 部分約定のまま失効した場合は残数量を運ぶ。
    [Fact]
    public async Task 部分約定のまま失効したら残数量を運ぶ()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        using var host = await BuildHostAsync(ledger);
        var decisionId = await ApproveCloseAsync(host, 100);

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.Expired, 30, 334m, Approved.AddHours(6),
            BrokerProvider.MoomooSimulate));

        var abandoned = session.Sent.MessagesOf<PositionCloseAbandoned>().Single();
        abandoned.FilledQuantity.Should().Be(30);
        abandoned.RemainingQuantity.Should().Be(70);

        await host.StopAsync();
    }

    // 明示的な取消（利用者操作・OrderCancelled）でも同じく発行する。
    [Fact]
    public async Task 取り消された手仕舞いでも発行される()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        using var host = await BuildHostAsync(ledger);
        var decisionId = await ApproveCloseAsync(host, 100);

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new OrderCancelled(decisionId, "ORD-1", "利用者による取消", Approved.AddMinutes(5)));

        session.Sent.MessagesOf<PositionCloseAbandoned>().Single()
            .RemainingQuantity.Should().Be(100);

        await host.StopAsync();
    }

    // 🔴 否定形: 全量約定した手仕舞いは「失効」ではない（残りが無い）。通知しない。
    [Fact]
    public async Task 全量約定した手仕舞いでは発行しない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        using var host = await BuildHostAsync(ledger);
        var decisionId = await ApproveCloseAsync(host, 100);

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.Filled, 100, 334m, Approved.AddMinutes(1),
            BrokerProvider.MoomooSimulate));

        session.Sent.MessagesOf<PositionCloseAbandoned>().Should().BeEmpty();

        await host.StopAsync();
    }

    // 🔴 否定形: 非終端（受付・部分約定の途中経過）では発行しない。まだ約定し得る。
    [Fact]
    public async Task 非終端の途中経過では発行しない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        using var host = await BuildHostAsync(ledger);
        var decisionId = await ApproveCloseAsync(host, 100);

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.PartiallyFilled, 30, 334m, Approved.AddMinutes(1),
            BrokerProvider.MoomooSimulate));

        session.Sent.MessagesOf<PositionCloseAbandoned>().Should().BeEmpty();

        await host.StopAsync();
    }

    // 🔴 否定形: エントリー（Open）の失効は手仕舞いではない。建玉は生じていない。
    [Fact]
    public async Task エントリーの失効では発行しない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        using var host = await BuildHostAsync(ledger);
        var decisionId = Guid.NewGuid();
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new OrderApproved(decisionId, OpenIntent(100), 100, Approved));

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.Expired, 0, 0m, Approved.AddHours(6),
            BrokerProvider.MoomooSimulate));

        session.Sent.MessagesOf<PositionCloseAbandoned>().Should().BeEmpty();

        await host.StopAsync();
    }

    // 🔴 冪等: 終端の記録は単調である（最初の終端が真）。再配送で通知を撃ち直さない。
    [Fact]
    public async Task 終端の再配送では二度発行しない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        using var host = await BuildHostAsync(ledger);
        var decisionId = await ApproveCloseAsync(host, 100);
        var executed = new OrderExecuted(
            decisionId, "ORD-1", OrderStatus.Cancelled, 0, 0m, Approved.AddMinutes(5),
            BrokerProvider.MoomooSimulate);

        (await host.TrackActivityForTest().InvokeMessageAndWaitAsync(executed))
            .Sent.MessagesOf<PositionCloseAbandoned>().Should().ContainSingle();

        (await host.TrackActivityForTest().InvokeMessageAndWaitAsync(executed))
            .Sent.MessagesOf<PositionCloseAbandoned>().Should().BeEmpty("MarkTerminal は単調（最初の終端が真）");

        await host.StopAsync();
    }

    // 🔴 相関する承認が無い注文（台帳の語彙に無い）では発行しない。
    [Fact]
    public async Task 台帳に承認が無い注文では発行しない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        using var host = await BuildHostAsync(ledger);

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderExecuted(
            Guid.NewGuid(), "ORD-1", OrderStatus.Expired, 0, 0m, Approved, BrokerProvider.MoomooSimulate));

        session.Sent.MessagesOf<PositionCloseAbandoned>().Should().BeEmpty();

        await host.StopAsync();
    }
}
