using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.PollOrderFills;
using OrderExecutionService.Features.OrderExecution.RecordTradeExpenses;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Hosted;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 FR-10, FR-05, FR-11, #1339（利用者裁定 2026-10-14・最小是正）, IADR-0536 決定2〜4: 約定追跡の常駐が
//   - 追跡を打ち切った承認経路の決済を間隔ごとに 1 回照会し直し、ブローカーが終端と答えたら解放する（OrderExecuted を発行し INF を残す）。
//   - 発注から一定時間たって非終端の承認経路の決済を、間隔ごとに 1 件ずつ WRN で出す（0 件なら出さない。間隔の内側では繰り返さない）。
// 作業仕様書 20261014_1339_close-limit-tif-and-release §受け入れ基準 6・7。
public class PendingCloseRecheckAndReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 14, 0, 0, TimeSpan.Zero);
    private const string ServiceName = "ai-stock-trading.order-execution-service";

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
    }

    private sealed class Broker : IBrokerAdapter
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public Dictionary<string, BrokerOrder?> Orders { get; } = [];

        public List<string> Queried { get; } = [];

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default)
        {
            Queried.Add(orderId);
            return Task.FromResult(Orders.TryGetValue(orderId, out var o) ? o : null);
        }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new InvalidOperationException("追跡は発注しない。");

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) =>
            throw new InvalidOperationException("追跡は取消しない。");
    }

    private static readonly OrderIntent CloseIntent =
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate, 13, 347.93m,
            PositionEffect.Close);

    private static ExecutionRecord Close(string orderId, DateTimeOffset at, Guid decisionId,
        OrderApprovalOrigin? origin = OrderApprovalOrigin.TradeDecision) =>
        new(decisionId, orderId, "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, PositionEffect.Close,
            13, 347.93m, 0, 0m, OrderStatus.Accepted, 0m, at, ApprovalOrigin: origin);

    private static Task<IHost> BuildHostAsync(IBrokerAdapter broker, IExecutedOrderStore store, IClock clock) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(clock);
                opts.Services.AddSingleton(broker);
                opts.Services.AddSingleton(store);
                opts.Services.AddScoped<OrderFillPoller>();
                opts.Services.AddSingleton<IOrderExpenseSource, UnsuppliedOrderExpenseSource>();
                opts.Services.AddScoped<TradeExpenseRecordingService>();
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static OrderFillPollingService BuildService(IHost host, RecordingLogger logger) =>
        new(host.Services.GetRequiredService<IServiceScopeFactory>(),
            host.Services.GetRequiredService<IWolverineRuntime>(),
            Options.Create(new FillPollingOptions()),
            logger);

    // T-10-2657: 追跡を打ち切った判断の決済を、間隔（既定 10 分）ごとに 1 回だけ照会し直す。ブローカーが失効（Rejected）と答えたら
    // OrderExecuted を発行して記録を終端にし、解放を INF で残す。生きている間（Accepted）は解放しない。
    [Fact]
    public async Task T_10_2657_打ち切った決済を間隔ごとに照会し直し終端なら発行して解放を残す()
    {
        var store = new InMemoryExecutedOrderStore();
        var broker = new Broker();
        var clock = new MutableClock();
        var decisionId = Guid.NewGuid();
        var placedAt = Now.AddHours(-24.5);
        store.Save(Close("7860264464660206071", placedAt, decisionId));
        store.MarkTrackingAbandoned("7860264464660206071", placedAt).Should().BeTrue();
        broker.Orders["7860264464660206071"] = new BrokerOrder(
            "7860264464660206071", CloseIntent, OrderStatus.Accepted, 0, 0m, default, null);
        using var host = await BuildHostAsync(broker, store, clock);
        var logger = new RecordingLogger();
        var service = BuildService(host, logger);
        Func<IMessageContext, Task> poll = async _ => await service.PollOnceAsync(CancellationToken.None);

        // 1 回目: 生きている → 解放しない。
        var first = await host.TrackActivityForTest().ExecuteAndWaitAsync(poll);
        first.Sent.MessagesOf<OrderExecuted>().Should().BeEmpty();
        broker.Queried.Should().Equal(["7860264464660206071"]);

        // 間隔の内側（9 分後）: 照会し直さない（失効していても）。
        broker.Orders["7860264464660206071"] = new BrokerOrder(
            "7860264464660206071", CloseIntent, OrderStatus.Rejected, 0, 0m, default, clock.UtcNow);
        clock.UtcNow = Now.AddMinutes(9);
        var inside = await host.TrackActivityForTest().ExecuteAndWaitAsync(poll);
        inside.Sent.MessagesOf<OrderExecuted>().Should().BeEmpty("間隔の内側では照会し直さない");
        broker.Queried.Should().HaveCount(1);

        // 間隔を過ぎた（10 分後）: 失効を観測して解放する。
        clock.UtcNow = Now.AddMinutes(10);
        var after = await host.TrackActivityForTest().ExecuteAndWaitAsync(poll);
        after.Sent.MessagesOf<OrderExecuted>().Should().ContainSingle(m =>
            m.DecisionId == decisionId && m.OrderId == "7860264464660206071" && m.Status == OrderStatus.Rejected);
        store.FindByDecisionId(decisionId)!.Status.Should().Be(OrderStatus.Rejected);
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Information
            && e.Message.Contains("処理中の決済を解放しました", StringComparison.Ordinal)
            && e.Message.Contains("7860264464660206071", StringComparison.Ordinal)
            && e.Message.Contains(decisionId.ToString(), StringComparison.Ordinal));

        await host.StopAsync();
    }

    // T-10-2658: 発注から 15 分以上たって非終端の承認経路の決済を、間隔（既定 15 分）ごとに 1 件ずつ WRN で出す。
    // 15 分未満・出どころの無い決済・終端は出さない。0 件なら何も出さない。間隔の内側では繰り返さない。
    [Fact]
    public async Task T_10_2658_未約定の決済を間隔ごとにWRNで出す()
    {
        var store = new InMemoryExecutedOrderStore();
        var broker = new Broker();
        var clock = new MutableClock();
        var stale = Guid.NewGuid();
        store.Save(Close("ORD-STALE", Now.AddMinutes(-16), stale));
        store.Save(Close("ORD-FRESH", Now.AddMinutes(-14), Guid.NewGuid()));
        store.Save(Close("ORD-S1", Now.AddMinutes(-40), Guid.NewGuid(), origin: null));
        broker.Orders["ORD-STALE"] = new BrokerOrder("ORD-STALE", CloseIntent, OrderStatus.Accepted, 0, 0m, default, null);
        broker.Orders["ORD-FRESH"] = new BrokerOrder("ORD-FRESH", CloseIntent, OrderStatus.Accepted, 0, 0m, default, null);
        broker.Orders["ORD-S1"] = new BrokerOrder("ORD-S1", CloseIntent, OrderStatus.Accepted, 0, 0m, default, null);
        using var host = await BuildHostAsync(broker, store, clock);
        var logger = new RecordingLogger();
        var service = BuildService(host, logger);

        await service.PollOnceAsync(CancellationToken.None);

        var warned = logger.Entries
            .Where(e => e.Level == LogLevel.Warning && e.Message.StartsWith("未約定の決済が残っています", StringComparison.Ordinal))
            .ToList();
        var line = warned.Should().ContainSingle().Subject.Message;
        line.Should().Contain("ORD-STALE").And.Contain(stale.ToString()).And.Contain("AAPL")
            .And.Contain("約定 0/13").And.Contain("発注から 16 分").And.Contain("追跡中");

        // 間隔の内側（14 分後）: 繰り返さない（ORD-FRESH も 15 分を超えたが、次の間隔まで出さない）。
        logger.Entries.Clear();
        clock.UtcNow = Now.AddMinutes(14);
        await service.PollOnceAsync(CancellationToken.None);
        logger.Entries.Should().NotContain(e => e.Message.StartsWith("未約定の決済が残っています", StringComparison.Ordinal));

        // 間隔を過ぎた（15 分後）: 2 件。ORD-STALE が約定して終端なら出さない。
        broker.Orders["ORD-STALE"] = new BrokerOrder("ORD-STALE", CloseIntent, OrderStatus.Filled, 13, 348m, default, clock.UtcNow);
        clock.UtcNow = Now.AddMinutes(15);
        await service.PollOnceAsync(CancellationToken.None);
        logger.Entries
            .Where(e => e.Level == LogLevel.Warning && e.Message.StartsWith("未約定の決済が残っています", StringComparison.Ordinal))
            .Select(e => e.Message)
            .Should().ContainSingle().Which.Should().Contain("ORD-FRESH", "約定した決済は出さない（後の端）");

        // 0 件なら何も出さない。
        broker.Orders["ORD-FRESH"] = new BrokerOrder("ORD-FRESH", CloseIntent, OrderStatus.Cancelled, 0, 0m, default, clock.UtcNow);
        logger.Entries.Clear();
        clock.UtcNow = Now.AddMinutes(30);
        await service.PollOnceAsync(CancellationToken.None);
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);

        await host.StopAsync();
    }

    private sealed record Entry(LogLevel Level, string Message);

    private sealed class RecordingLogger : ILogger<OrderFillPollingService>
    {
        public List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
                Entries.Add(new Entry(logLevel, formatter(state, exception)));
        }
    }
}
