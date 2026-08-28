using RiskManagementService.Application.Adapters;
using RiskManagementService.Application.Ports;
using RiskManagementService.Domain.Manipulation;
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

namespace RiskManagementService.Infrastructure.Tests;

// FR-19, #154, IADR-0067: 注文系イベントを購読して注文アクティビティへ射影する Consumer 群を
// MassTransit テストハーネス + インメモリ射影ストアで検証する。射影後に窓として読めることまで確かめる。
public class OrderActivityProjectionConsumersTests
{
    private static readonly DateTimeOffset Base = new(2026, 7, 18, 6, 0, 0, TimeSpan.Zero);

    private static OrderIntent Intent(int qty = 10) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, qty, 3_000m);

    // ADR-0013, IADR-0129, #354: MassTransit のテストハーネスから Wolverine.Tracking へ移行した。
    // 明示登録（AddConsumer<T>）は「規約発見を止めて対象型だけを含める」形へ写す
    // （テストの対象範囲を旧テストと同一に保つ）。実ブローカへは接続しない。
    private static Task<IHost> BuildHostAsync(InMemoryOrderActivityStore store) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IOrderActivityStore>(store);
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<OrderApprovedActivityHandler>()
                    .IncludeType<OrderExecutedActivityHandler>()
                    .IncludeType<OrderModifiedActivityHandler>()
                    .IncludeType<OrderCancelledActivityHandler>();
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static OrderActivityWindow Window(InMemoryOrderActivityStore store) =>
        store.GetRecentActivity("AAPL", Market.UnitedStates, Base.AddMinutes(1), TimeSpan.FromMinutes(5));

    [Fact]
    public async Task 承認から取消までを購読し注文アクティビティへ射影する()
    {
        var store = new InMemoryOrderActivityStore();
        using var host = await BuildHostAsync(store);

        var decisionId = Guid.NewGuid();
        var session1 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderApproved(decisionId, Intent(10), 10, Base));
        session1.Executed.MessagesOf<OrderApproved>().Should().NotBeEmpty();
        var session2 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderModified(decisionId, "ORD-1", 10, 3_000m, 6, 2_950m, "縮小", Base.AddSeconds(10)));
        session2.Executed.MessagesOf<OrderModified>().Should().NotBeEmpty();
        var session3 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderCancelled(decisionId, "ORD-1", "pause 強制取消", Base.AddSeconds(20)));
        session3.Executed.MessagesOf<OrderCancelled>().Should().NotBeEmpty();

        var record = Window(store).Records.Should().ContainSingle().Subject;
        record.AmendmentCount.Should().Be(1);
        record.Quantity.Should().Be(6);
        record.Status.Should().Be(OrderStatus.Cancelled);
        record.IsCancelledWithoutFill.Should().BeTrue();

        await host.StopAsync();
    }

    [Fact]
    public async Task 約定は状態と約定数を射影する()
    {
        var store = new InMemoryOrderActivityStore();
        using var host = await BuildHostAsync(store);

        var decisionId = Guid.NewGuid();
        var session1 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderApproved(decisionId, Intent(10), 10, Base));
        session1.Executed.MessagesOf<OrderApproved>().Should().NotBeEmpty();
        var session2 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderExecuted(decisionId, "ORD-1", OrderStatus.Filled, 10, 3_010m, Base.AddSeconds(1), BrokerProvider.MoomooSimulate));
        session2.Executed.MessagesOf<OrderExecuted>().Should().NotBeEmpty();

        var record = Window(store).Records.Should().ContainSingle().Subject;
        record.Status.Should().Be(OrderStatus.Filled);
        record.FilledQuantity.Should().Be(10);
        record.IsFilledOrPartial.Should().BeTrue();

        await host.StopAsync();
    }

    [Fact]
    public async Task 相関する承認が無い約定は射影されない()
    {
        var store = new InMemoryOrderActivityStore();
        using var host = await BuildHostAsync(store);

        var session1 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderExecuted(Guid.NewGuid(), "ORD-1", OrderStatus.Filled, 10, 3_010m, Base, BrokerProvider.MoomooSimulate));
        session1.Executed.MessagesOf<OrderExecuted>().Should().NotBeEmpty();

        Window(store).Records.Should().BeEmpty();

        await host.StopAsync();
    }
}
