using AiStockTrading.OrderExecution.Application.Adapters;
using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.OrderExecution.Application.Services;
using AiStockTrading.OrderExecution.Infrastructure.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using AppSvc = AiStockTrading.OrderExecution.Application.Services.OrderExecutionService;

namespace AiStockTrading.OrderExecution.Infrastructure.Tests;

// #154, FR-05, FR-19, IADR-0067: 訂正・取消の発行を Wolverine のテストハーネス（Wolverine.Tracking）で検証する。
// ADR-0013, IADR-0129, #354: harness.Published → session.Sent への移行。表明の意味は同じ。
// 本 PR では駆動元（#141/#152・時限取消）を実装しないため、ここが配管の終端（発行）の担保になる。
public class OrderAmendmentDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 6, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // #331, IADR-0210: 保護逆指値の同時発注（Open 専用）を巻き込まないよう Close 注文で発注済み状態を作る。
    private static OrderIntent Intent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.InternalPaper,
            10, 3000m, PositionEffect.Close);

    // 非終端の注文を扱うため immediateFill=false のペーパーブローカで組む（IADR-0067）。
    private const string ServiceName = "ai-stock-trading.order-execution-service";

    private static Task<IHost> BuildHostAsync(PaperBrokerAdapter broker, InMemoryExecutedOrderStore executedOrders) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, FakeClock>();
                opts.Services.AddSingleton<IBrokerAdapter>(broker);
                opts.Services.AddSingleton<IOrderAmendmentBroker>(broker);
                opts.Services.AddSingleton<IExecutedOrderStore>(executedOrders);
                opts.Services.AddSingleton<IOrderLifecycleStore, InMemoryOrderLifecycleStore>();
                // IMessageBus が scoped のため、本番配線（Program.cs）と同じく scoped で登録する。
                opts.Services.AddScoped<OrderAmendmentService>();
                opts.Services.AddScoped<OrderAmendmentDispatcher>();

                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static async Task<Guid> PlaceAsync(PaperBrokerAdapter broker, InMemoryExecutedOrderStore store)
    {
        var decisionId = Guid.NewGuid();
        var execution = new AppSvc(broker, store, new InMemoryOrderReservationStore(), new FakeClock());
        await execution.ExecuteAsync(new OrderApproved(decisionId, Intent(), 10, Now));
        return decisionId;
    }

    [Fact]
    public async Task 取消すると_OrderCancelled_が発行される()
    {
        var broker = new PaperBrokerAdapter(immediateFill: false);
        var executedOrders = new InMemoryExecutedOrderStore();
        using var host = await BuildHostAsync(broker, executedOrders);
        var decisionId = await PlaceAsync(broker, executedOrders);

        using var scope = host.Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<OrderAmendmentDispatcher>();
        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(
            _ => dispatcher.CancelAsync(decisionId, reason: "pause による強制取消"));

        session.Sent.MessagesOf<OrderCancelled>().Should().NotBeEmpty();
        var published = session.Sent.MessagesOf<OrderCancelled>().Single();
        published.DecisionId.Should().Be(decisionId);
        published.Reason.Should().Be("pause による強制取消");
        published.CancelledAt.Should().Be(Now);
    }

    [Fact]
    public async Task 訂正すると_OrderModified_が訂正前後の値つきで発行される()
    {
        var broker = new PaperBrokerAdapter(immediateFill: false);
        var executedOrders = new InMemoryExecutedOrderStore();
        using var host = await BuildHostAsync(broker, executedOrders);
        var decisionId = await PlaceAsync(broker, executedOrders);

        using var scope = host.Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<OrderAmendmentDispatcher>();
        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(
            _ => dispatcher.ModifyAsync(decisionId, quantity: 4, price: 2950m, reason: "数量縮小"));

        session.Sent.MessagesOf<OrderModified>().Should().NotBeEmpty();
        var published = session.Sent.MessagesOf<OrderModified>().Single();
        published.DecisionId.Should().Be(decisionId);
        published.PreviousQuantity.Should().Be(10);
        published.PreviousPrice.Should().Be(3000m);
        published.Quantity.Should().Be(4);
        published.Price.Should().Be(2950m);
    }

    [Fact]
    public async Task 適用に失敗したら発行しない()
    {
        // 不整合（未適用なのに取消イベントだけが流れる）を作らない。
        var broker = new PaperBrokerAdapter(immediateFill: false);
        var executedOrders = new InMemoryExecutedOrderStore();
        using var host = await BuildHostAsync(broker, executedOrders);

        using var scope = host.Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<OrderAmendmentDispatcher>();
        var act = () => dispatcher.CancelAsync(Guid.NewGuid(), reason: "未知の判断ID");

        // 例外が投げられること自体が前提条件なので、追跡ブロックの中で捕捉して表明する
        // （発行が起きていないことを同じ追跡セッションで見るため）。
        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(_ => ShouldThrowAsync(act));

        session.Sent.MessagesOf<OrderCancelled>().Should().BeEmpty();
    }

    // 追跡ブロック内で「例外が投げられる」ことを表明する補助（元テストの act.Should().ThrowAsync と同じ）。
    private static async Task ShouldThrowAsync(Func<Task<OrderCancelled>> act) =>
        await act.Should().ThrowAsync<InvalidOperationException>();
}
