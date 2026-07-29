using AiStockTrading.OrderExecution.Application.Adapters;
using AiStockTrading.OrderExecution.Application.Polling;
using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.OrderExecution.Domain;
using AiStockTrading.OrderExecution.Worker.Composable.Adapters;
using AiStockTrading.OrderExecution.Worker.Composable.Polling;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiStockTrading.OrderExecution.Worker.Tests;

// #270, FR-05, FR-10, IADR-0113: 約定状態の追跡ポーリングの定期実行。終端化・進捗の OrderExecuted 発行と、
// 無効時に一切走査しないことを MassTransit テストハーネスで検証する。
public class OrderFillPollingServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 6, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // moomoo 相当の非同期約定を模したブローカ。照会のたびに次の状態を返す。
    private sealed class SequenceBroker(params BrokerOrder?[] responses) : IBrokerAdapter
    {
        private int _index;

        public int QueryCount { get; private set; }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default)
        {
            QueryCount++;
            var response = _index < responses.Length ? responses[_index] : responses[^1];
            _index++;
            return Task.FromResult(response);
        }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new InvalidOperationException("追跡は発注しない。");

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) =>
            throw new InvalidOperationException("追跡は取消しない。");
    }

    private static OrderIntent Intent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper, 1_000, 340m);

    private static ExecutionRecord Dispatched(Guid decisionId) =>
        new(decisionId, "ORD-1", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            PositionEffect.Open, 1_000, 340m, 0, 0m, OrderStatus.Accepted, 0m, Now.AddMinutes(-3));

    private static ServiceProvider BuildProvider(IBrokerAdapter broker, IExecutedOrderStore store) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClock, FakeClock>()
            .AddSingleton(broker)
            .AddSingleton(store)
            .AddScoped<OrderFillPoller>()
            .AddMassTransitTestHarness()
            .BuildServiceProvider(true);

    private static OrderFillPollingService BuildService(ServiceProvider provider, FillPollingOptions options) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IBus>(),
            Options.Create(options),
            NullLogger<OrderFillPollingService>.Instance);

    [Fact]
    public async Task 終端化した約定はOrderExecutedとして発行される()
    {
        var store = new InMemoryExecutedOrderStore();
        var decisionId = Guid.NewGuid();
        store.Save(Dispatched(decisionId));
        var broker = new SequenceBroker(
            new BrokerOrder("ORD-1", Intent(), OrderStatus.Filled, 1_000, 341m, default, Now));
        await using var provider = BuildProvider(broker, store);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var service = BuildService(provider, new FillPollingOptions());

        var result = await service.PollOnceAsync(CancellationToken.None);

        result.Terminalized.Should().Be(1);
        (await harness.Published.Any<OrderExecuted>(x =>
            x.Context.Message.DecisionId == decisionId
            && x.Context.Message.Status == OrderStatus.Filled
            && x.Context.Message.FilledQuantity == 1_000)).Should().BeTrue();

        await harness.Stop();
    }

    [Fact]
    public async Task 照会不能では何も発行されず記録も変わらない()
    {
        var store = new InMemoryExecutedOrderStore();
        var decisionId = Guid.NewGuid();
        store.Save(Dispatched(decisionId));
        // 照会結果なし（当日一覧に無い・アダプタが例外を握って null に倒した）を 1 回返す。
        await using var provider = BuildProvider(new SequenceBroker([null]), store);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var service = BuildService(provider, new FillPollingOptions());

        var result = await service.PollOnceAsync(CancellationToken.None);

        result.Unknown.Should().Be(1);
        (await harness.Published.Any<OrderExecuted>()).Should().BeFalse();
        store.FindByDecisionId(decisionId)!.Status.Should().Be(OrderStatus.Accepted);

        await harness.Stop();
    }

    [Fact]
    public async Task 無効時はExecuteAsyncが照会せず即座に戻る()
    {
        var store = new InMemoryExecutedOrderStore();
        store.Save(Dispatched(Guid.NewGuid()));
        var broker = new SequenceBroker(
            new BrokerOrder("ORD-1", Intent(), OrderStatus.Filled, 1_000, 341m, default, Now));
        await using var provider = BuildProvider(broker, store);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var service = BuildService(provider, new FillPollingOptions { Enabled = false });

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        broker.QueryCount.Should().Be(0);
        (await harness.Published.Any<OrderExecuted>()).Should().BeFalse();

        await harness.Stop();
    }

    // #270: 既定は有効（IADR-0113）。統制の必要条件であり、既定オフでは「統制が効かない状態」を出荷することになる。
    [Fact]
    public void 既定は有効で巡回間隔は短周期にクランプされる()
    {
        var options = new FillPollingOptions();

        options.Enabled.Should().BeTrue();
        options.Interval.Should().Be(TimeSpan.FromSeconds(30));
        options.MaxTracking.Should().Be(TimeSpan.FromHours(24));

        new FillPollingOptions { IntervalSeconds = 0 }.Interval.Should().Be(TimeSpan.FromSeconds(5));
        new FillPollingOptions { IntervalSeconds = 99_999 }.Interval.Should().Be(TimeSpan.FromHours(1));
        new FillPollingOptions { MaxTrackingHours = 0 }.MaxTracking.Should().Be(TimeSpan.FromHours(1));
        new FillPollingOptions { BatchSize = 0 }.EffectiveBatchSize.Should().Be(1);
    }

    // #270, IADR-0113: moomoo の状態遷移（Submitted → FilledPart → FilledAll）が追跡経由で
    // Accepted → PartiallyFilled → Filled として台帳へ届くことを、実アダプタの写像で通しで確認する。
    [Fact]
    public async Task moomoo状態遷移が追跡経由で約定として届く()
    {
        var store = new InMemoryExecutedOrderStore();
        var decisionId = Guid.NewGuid();
        store.Save(Dispatched(decisionId));
        var client = new StubMoomooTradeClient();
        var adapter = new MoomooBrokerAdapter(client);
        await using var provider = BuildProvider(adapter, store);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var service = BuildService(provider, new FillPollingOptions());

        client.Next = new MoomooOrderResult("ORD-1", MoomooOrderState.FilledPart, 300, 340.5m);
        var first = await service.PollOnceAsync(CancellationToken.None);
        first.Executed.Should().ContainSingle(e => e.Status == OrderStatus.PartiallyFilled && e.FilledQuantity == 300);

        client.Next = new MoomooOrderResult("ORD-1", MoomooOrderState.FilledAll, 1_000, 340.8m);
        var second = await service.PollOnceAsync(CancellationToken.None);
        second.Executed.Should().ContainSingle(e => e.Status == OrderStatus.Filled && e.FilledQuantity == 1_000);

        store.FindByDecisionId(decisionId)!.Status.Should().Be(OrderStatus.Filled);

        await harness.Stop();
    }

    // 照会だけを返す最小の OpenD スタブ（発注・取消は追跡経路では呼ばれない）。
    private sealed class StubMoomooTradeClient : IMoomooTradeClient
    {
        public MoomooOrderResult? Next { get; set; }

        public Task<MoomooOrderResult> PlaceOrderAsync(
            MoomooOrderRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("追跡は発注しない。");

        public Task<MoomooOrderResult?> QueryOrderAsync(string orderId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Next);

        public Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("追跡は取消しない。");

        public Task<MoomooOrderSnapshot?> FindOrderByClientIdAsync(
            string clientOrderId, DateTimeOffset reservedAtUtc, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("追跡は remark 照合を用いない。");
    }
}
