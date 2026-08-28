using OrderExecutionService.Application.Adapters;
using OrderExecutionService.Application.Polling;
using OrderExecutionService.Application.Ports;
using OrderExecutionService.Domain;
using OrderExecutionService.Infrastructure.Adapters;
using OrderExecutionService.Infrastructure.Polling;
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
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace OrderExecutionService.Infrastructure.Tests;

// #270, FR-05, FR-10, IADR-0113: 約定状態の追跡ポーリングの定期実行。終端化・進捗の OrderExecuted 発行と、
// 無効時に一切走査しないことを Wolverine のテストハーネス（Wolverine.Tracking）で検証する
// （ADR-0013 / IADR-0129 / #354。harness.Published → session.Sent。表明の意味は同じ）。
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

        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

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
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 1_000, 340m);

    private static ExecutionRecord Dispatched(Guid decisionId) =>
        new(decisionId, "ORD-1", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            PositionEffect.Open, 1_000, 340m, 0, 0m, OrderStatus.Accepted, 0m, Now.AddMinutes(-3));

    private const string ServiceName = "ai-stock-trading.order-execution-service";

    // 本番と同じ配線（キュー名・fan-out・再試行・DLQ）を用い、送信先だけ stub へ倒す。
    private static Task<IHost> BuildHostAsync(IBrokerAdapter broker, IExecutedOrderStore store) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, FakeClock>();
                opts.Services.AddSingleton(broker);
                opts.Services.AddSingleton(store);
                opts.Services.AddScoped<OrderFillPoller>();

                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static OrderFillPollingService BuildService(IHost host, FillPollingOptions options) =>
        new(host.Services.GetRequiredService<IServiceScopeFactory>(),
            // 常駐（singleton）であり、Wolverine の IMessageBus（scoped）は注入できない。
            host.Services.GetRequiredService<IWolverineRuntime>(),
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
        using var host = await BuildHostAsync(broker, store);
        var service = BuildService(host, new FillPollingOptions());

        OrderFillPollResult result = null!;
        Func<IMessageContext, Task> poll = async _ => result = await service.PollOnceAsync(CancellationToken.None);
        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(poll);

        result.Terminalized.Should().Be(1);
        session.Sent.MessagesOf<OrderExecuted>().Should().Contain(m =>
            m.DecisionId == decisionId
            && m.Status == OrderStatus.Filled
            && m.FilledQuantity == 1_000);

        await host.StopAsync();
    }

    [Fact]
    public async Task 照会不能では何も発行されず記録も変わらない()
    {
        var store = new InMemoryExecutedOrderStore();
        var decisionId = Guid.NewGuid();
        store.Save(Dispatched(decisionId));
        // 照会結果なし（当日一覧に無い・アダプタが例外を握って null に倒した）を 1 回返す。
        using var host = await BuildHostAsync(new SequenceBroker([null]), store);
        var service = BuildService(host, new FillPollingOptions());

        OrderFillPollResult result = null!;
        Func<IMessageContext, Task> poll = async _ => result = await service.PollOnceAsync(CancellationToken.None);
        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(poll);

        result.Unknown.Should().Be(1);
        session.Sent.MessagesOf<OrderExecuted>().Should().BeEmpty();
        store.FindByDecisionId(decisionId)!.Status.Should().Be(OrderStatus.Accepted);

        await host.StopAsync();
    }

    [Fact]
    public async Task 無効時はExecuteAsyncが照会せず即座に戻る()
    {
        var store = new InMemoryExecutedOrderStore();
        store.Save(Dispatched(Guid.NewGuid()));
        var broker = new SequenceBroker(
            new BrokerOrder("ORD-1", Intent(), OrderStatus.Filled, 1_000, 341m, default, Now));
        using var host = await BuildHostAsync(broker, store);
        var service = BuildService(host, new FillPollingOptions { Enabled = false });

        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(_ => StartAndStopAsync(service));

        broker.QueryCount.Should().Be(0);
        session.Sent.MessagesOf<OrderExecuted>().Should().BeEmpty();

        await host.StopAsync();
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
        var adapter = new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate);
        using var host = await BuildHostAsync(adapter, store);
        var service = BuildService(host, new FillPollingOptions());

        client.Next = new MoomooOrderResult("ORD-1", MoomooOrderState.FilledPart, 300, 340.5m);
        var first = await service.PollOnceAsync(CancellationToken.None);
        first.Executed.Should().ContainSingle(e => e.Status == OrderStatus.PartiallyFilled && e.FilledQuantity == 300);

        client.Next = new MoomooOrderResult("ORD-1", MoomooOrderState.FilledAll, 1_000, 340.8m);
        var second = await service.PollOnceAsync(CancellationToken.None);
        second.Executed.Should().ContainSingle(e => e.Status == OrderStatus.Filled && e.FilledQuantity == 1_000);

        store.FindByDecisionId(decisionId)!.Status.Should().Be(OrderStatus.Filled);

        await host.StopAsync();
    }

    // 常駐を起動して止めるだけの補助（元テストと呼び出し順は同じ）。
    private static async Task StartAndStopAsync(OrderFillPollingService service)
    {
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
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

        public Task<IReadOnlyList<MoomooPositionSnapshot>> GetPositionsAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("約定追跡は建玉照会を用いない。");

        // #375, ADR-0021: 約定追跡は口座種別を用いない。
        public Task<MoomooAccountType?> GetAccountTypeAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("約定追跡は口座種別照会を用いない。");
    }
}
