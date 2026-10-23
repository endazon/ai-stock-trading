extern alias RiskManagementWorker;

using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.PollOrderFills;
using OrderExecutionService.Features.OrderExecution.RecordTradeExpenses;
using OrderExecutionService.Hosted;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Infrastructure.Persistence;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Xunit;
using RmLedger = RiskManagementWorker::RiskManagementService.Infrastructure.Persistence.InMemoryPortfolioLedgerStore;
using RmLedgerHandler = RiskManagementWorker::RiskManagementService.Infrastructure.Steps.OrderExecutedLedgerHandler;

namespace OrderExecutionService.Tests;

// 🔴 NFR, FR-05, FR-10, #1442（親 #1423 M-4・試験の穴 5）, IADR-0573 決定2: 約定追跡の Worker が OrderExecuted の発行に失敗しても、
// 記録の発行待ちの印が残り、プロセスを替えた次の巡回で再発行され、リスク管理の台帳には 1 回だけ載る。発行に成功した約定は再発行しない。
public class OrderExecutedRepublishPollingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 14, 0, 0, TimeSpan.Zero);
    private const string ServiceName = "ai-stock-trading.order-execution-service";

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class FilledBroker : IBrokerAdapter
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(new BrokerOrder(orderId, Intent(), OrderStatus.Filled, 100, 341m, default, Now.AddMinutes(-1)));

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new InvalidOperationException("追跡は発注しない。");

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) =>
            throw new InvalidOperationException("追跡は取消しない。");
    }

    // 発行待ちの印を倒す書き込みだけが失敗する店（発行には成功したのに印が残る＝同じ約定が 2 回届く経路）。
    private sealed class MarkFailingStore(InMemoryExecutedOrderStore inner) : IExecutedOrderStore
    {
        public void Save(ExecutionRecord record) => inner.Save(record);
        public IReadOnlyList<ExecutionRecord> GetAll() => inner.GetAll();
        public ExecutionRecord? FindByDecisionId(Guid decisionId) => inner.FindByDecisionId(decisionId);
        public IReadOnlyList<ExecutionRecord> FindPendingSince(DateTimeOffset since, int batchSize) => inner.FindPendingSince(since, batchSize);
        public IReadOnlyList<ExecutionRecord> FindPendingByOrderIds(IReadOnlyCollection<string> orderIds) => inner.FindPendingByOrderIds(orderIds);
        public bool RenewTracking(string orderId, DateTimeOffset trackedFrom) => inner.RenewTracking(orderId, trackedFrom);
        public bool UpdateOutcome(string orderId, OrderStatus status, int filledQuantity, decimal averagePrice, decimal slippageRatio, DateTimeOffset executedAt) =>
            inner.UpdateOutcome(orderId, status, filledQuantity, averagePrice, slippageRatio, executedAt);
        public IReadOnlyList<ExecutionRecord> FindOrderExecutedPublishPending(int batchSize) => inner.FindOrderExecutedPublishPending(batchSize);
        public bool MarkOrderExecutedPublished(string orderId, OrderStatus status, int filledQuantity) =>
            throw new InvalidOperationException("DB 断（印を書けない）");
    }

    private static OrderIntent Intent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 100, 340m);

    private static ExecutionRecord Dispatched(Guid decisionId) =>
        new(decisionId, "ORD-1", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            PositionEffect.Open, 100, 340m, 0, 0m, OrderStatus.Accepted, 0m, Now.AddMinutes(-3));

    // 本番と同じ配線（キュー名・fan-out・再試行・DLQ）を用い、送信先だけ stub へ倒す（OrderFillPollingServiceTests と同じ）。
    // ホストを作り直すことが「プロセスの再起動」に当たる（記録の店だけを引き継ぐ＝EF なら DB）。
    // failPublish: OrderExecuted の発行だけが投げる（障害の注入）。経路の解決で投げるので、PublishAsync が例外で返る。
    private static Task<IHost> BuildHostAsync(IExecutedOrderStore store, bool failPublish = false) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, FakeClock>();
                opts.Services.AddSingleton<IBrokerAdapter, FilledBroker>();
                opts.Services.AddSingleton(store);
                opts.Services.AddScoped<OrderFillPoller>();
                opts.Services.AddSingleton<IOrderExpenseSource, UnsuppliedOrderExpenseSource>();
                opts.Services.AddScoped<TradeExpenseRecordingService>();
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
                if (failPublish)
                    opts.RouteWith(new FailingOrderExecutedRouting());
            })
            .StartAsync();

    // 発行の失敗の注入（Wolverine の RabbitMQ 送信は断でも投げないため、ブローカー断そのものは注入できない——実測。IADR-0573）。
    // 耐久アウトボックスでは封筒を DB へ書けないときに PublishAsync が投げる。それと同じ「発行が例外で返る」を経路の解決で起こす。
    private sealed class FailingOrderExecutedRouting : IMessageRoutingConvention
    {
        public void DiscoverListeners(IWolverineRuntime runtime, IReadOnlyList<Type> handledMessageTypes)
        {
        }

        public IEnumerable<Endpoint> DiscoverSenders(Type messageType, IWolverineRuntime runtime) =>
            messageType == typeof(OrderExecuted)
                ? throw new InvalidOperationException("発行の失敗の注入（OrderExecuted）")
                : [];
    }

    private static OrderFillPollingService BuildService(IHost host) =>
        new(host.Services.GetRequiredService<IServiceScopeFactory>(),
            host.Services.GetRequiredService<IWolverineRuntime>(),
            Options.Create(new FillPollingOptions()),
            NullLogger<OrderFillPollingService>.Instance);

    private static async Task<IReadOnlyList<OrderExecuted>> PollAndCaptureAsync(IHost host)
    {
        var service = BuildService(host);
        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(
            (Func<IMessageContext, Task>)(async _ => await service.PollOnceAsync(CancellationToken.None)));
        return [.. session.Sent.MessagesOf<OrderExecuted>()];
    }

    // 受け手（リスク管理の台帳）。承認行が先にあり（相関の前提）、届いた OrderExecuted をすべて適用する。
    private static (RmLedger Ledger, RmLedgerHandler Handler) Ledger(Guid decisionId)
    {
        var ledger = new RmLedger();
        ledger.AppendApproval(decisionId, Intent(), Now.AddMinutes(-4));
        return (ledger, new RmLedgerHandler(ledger, NullLogger<RmLedgerHandler>.Instance));
    }

    [Fact]
    public async Task 発行に失敗した約定は再起動後の巡回で再発行され台帳に1回だけ載る()
    {
        var store = new InMemoryExecutedOrderStore();
        var decisionId = Guid.NewGuid();
        store.Save(Dispatched(decisionId));
        var (ledger, handler) = Ledger(decisionId);

        // 1 つ目のプロセス: 記録を終端化した後、発行が投げる（巡回は例外で終わり、プロセスはここで落ちたとする）。
        using (var crashed = await BuildHostAsync(store, failPublish: true))
        {
            var publish = async () => await BuildService(crashed).PollOnceAsync(CancellationToken.None);
            await publish.Should().ThrowAsync<InvalidOperationException>().WithMessage("*発行の失敗の注入*");
        }

        store.FindByDecisionId(decisionId)!.Status.Should().Be(OrderStatus.Filled, "記録は発行より先に終端化している（M-4 の前提）");
        store.FindOrderExecutedPublishPending(10).Should().ContainSingle("発行し終えていないので印が残る");

        // 2 つ目のプロセス: 記録は終端で追跡の対象ではないが、印から再発行する。
        using var restarted = await BuildHostAsync(store);
        var delivered = await PollAndCaptureAsync(restarted);
        delivered.Should().ContainSingle().Which.Should().Match<OrderExecuted>(e =>
            e.DecisionId == decisionId && e.Status == OrderStatus.Filled && e.FilledQuantity == 100);
        foreach (var e in delivered)
            handler.Handle(e);

        // 発行に成功したので印は倒れ、次の巡回では何も出ない。
        store.FindOrderExecutedPublishPending(10).Should().BeEmpty();
        (await PollAndCaptureAsync(restarted)).Should().BeEmpty();

        ledger.GetFills().Should().ContainSingle().Which.Quantity.Should().Be(100);
    }

    [Fact]
    public async Task 発行に成功した約定は再発行しない()
    {
        var store = new InMemoryExecutedOrderStore();
        var decisionId = Guid.NewGuid();
        store.Save(Dispatched(decisionId));
        using var host = await BuildHostAsync(store);

        var first = await PollAndCaptureAsync(host);
        var second = await PollAndCaptureAsync(host);

        first.Should().ContainSingle();
        second.Should().BeEmpty("発行の後に印を倒しているので、終端の約定を撃ち直さない");
        store.FindOrderExecutedPublishPending(10).Should().BeEmpty();
    }

    [Fact]
    public async Task 印を書けず同じ約定が2回届いても台帳は1回だけ数える()
    {
        var inner = new InMemoryExecutedOrderStore();
        var decisionId = Guid.NewGuid();
        inner.Save(Dispatched(decisionId));
        var (ledger, handler) = Ledger(decisionId);
        using var host = await BuildHostAsync(new MarkFailingStore(inner));

        var first = await PollAndCaptureAsync(host);
        var second = await PollAndCaptureAsync(host);

        first.Should().ContainSingle();
        second.Should().ContainSingle("印を倒せなかったので再発行する（少なくとも 1 回）");
        foreach (var e in first.Concat(second))
            handler.Handle(e);

        // 累積数量の単調 upsert（OrderId 単位）と、終端は初回だけ（IADR-0113 / IADR-0117）。
        ledger.GetFills().Should().ContainSingle().Which.Quantity.Should().Be(100);
    }
}
