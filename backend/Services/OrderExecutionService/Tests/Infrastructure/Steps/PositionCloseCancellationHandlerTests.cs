using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.AmendOrder;
using OrderExecutionService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 FR-05, FR-10, FR-11, UC-06, #847, #768, IADR-0357: **OrderAmendmentDispatcher の呼び出し元**。
//
// #768 は「DI 登録だけで本番の呼び出し元が無い」を台帳へ載せていた（UnwiredDiRegistrationTests の既知一覧）。
// #847 はそれが実運用で実害になった最初の事例であり（板に残った手仕舞いを消す手段が moomoo アプリだけだった）、
// 本ハンドラがその配線そのものである。
public class PositionCloseCancellationHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 6, 0, 0, TimeSpan.Zero);
    private const string ServiceName = "ai-stock-trading.order-execution-service";

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private static OrderIntent Intent() =>
        new("SOXL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
            3381, 334.09m, PositionEffect.Close);

    // 取消の送信後に返す状態を注入できるブローカー（null＝照会できない＝不明）。
    private sealed class ScriptedCancelBroker(OrderStatus? afterCancel) : IBrokerAdapter
    {
        public int CancelCount { get; private set; }

        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            Task.FromResult(new BrokerOrder("order-1", intent, OrderStatus.Accepted, 0, 0m, Now, null));

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(afterCancel is { } status
                ? new BrokerOrder(orderId, Intent(), status, 0, 0m, Now, null)
                : null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            CancelCount++;
            return Task.CompletedTask;
        }
    }

    private static Task<IHost> BuildHostAsync(IBrokerAdapter broker, InMemoryExecutedOrderStore executedOrders) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, FakeClock>();
                opts.Services.AddSingleton<IBrokerAdapter>(broker);
                opts.Services.AddSingleton<IExecutedOrderStore>(executedOrders);
                opts.Services.AddSingleton<IOrderLifecycleStore, InMemoryOrderLifecycleStore>();
                // IMessageBus が scoped のため、本番配線（Program.cs）と同じく scoped で登録する。
                opts.Services.AddScoped<OrderAmendmentService>();
                opts.Services.AddScoped<OrderAmendmentDispatcher>();

                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                // ハンドラは本番の型そのものを使う（UseAiStockTradingRabbitMq は呼び出し元＝テストアセンブリを
                // application assembly に固定するため、対象型を明示的に含める）。
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<PositionCloseCancellationHandler>();
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static Guid SeedOrder(InMemoryExecutedOrderStore store)
    {
        var decisionId = Guid.NewGuid();
        store.Save(new OrderExecutionService.Domain.ExecutionRecord(
            decisionId, "order-1", "SOXL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            PositionEffect.Close, 3381, 334.09m, 0, 0m, OrderStatus.Accepted, 0m, Now));
        return decisionId;
    }

    [Fact]
    public async Task 利用者の取消要求がブローカーへ届き_OrderCancelled_が発行される()
    {
        var broker = new ScriptedCancelBroker(OrderStatus.Cancelled);
        var executedOrders = new InMemoryExecutedOrderStore();
        using var host = await BuildHostAsync(broker, executedOrders);
        var decisionId = SeedOrder(executedOrders);

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new PositionCloseCancellationRequested(
                decisionId, "SOXL", Market.UnitedStates, "owner-1", "指値が置いていかれた", Now));

        broker.CancelCount.Should().Be(1, "アプリ操作に逃がさない（#847 の受け入れ基準）");
        var published = session.Sent.MessagesOf<OrderCancelled>().Single();
        published.DecisionId.Should().Be(decisionId);
        published.Reason.Should().Contain("指値が置いていかれた");
    }

    // 🔴 否定形・最重要: 取消の結果が不明なら **OrderCancelled を出さない**（在庫を戻さない）。
    // 本当の終端は既存の約定追跡（OrderFillPoller）が OrderExecuted として運ぶ。
    [Fact]
    public async Task 取消の結果が不明なら在庫を戻すイベントを出さない()
    {
        var broker = new ScriptedCancelBroker(afterCancel: null);
        var executedOrders = new InMemoryExecutedOrderStore();
        using var host = await BuildHostAsync(broker, executedOrders);
        var decisionId = SeedOrder(executedOrders);

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new PositionCloseCancellationRequested(
                decisionId, "SOXL", Market.UnitedStates, "owner-1", "取消", Now));

        broker.CancelCount.Should().Be(1);
        session.Sent.MessagesOf<OrderCancelled>().Should().BeEmpty(
            "不明のまま在庫を戻すと同じ建玉に 2 本目の決済が並ぶ（二重決済でショート化）");
    }
}
