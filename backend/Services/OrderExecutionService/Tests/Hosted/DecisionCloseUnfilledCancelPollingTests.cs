using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.AmendOrder;
using OrderExecutionService.Features.OrderExecution.CancelUnfilledDecisionCloses;
using OrderExecutionService.Features.OrderExecution.PollOrderFills;
using OrderExecutionService.Features.OrderExecution.RecordTradeExpenses;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Hosted;
using OrderExecutionService.Infrastructure.ExternalServices;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Composition;
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

namespace OrderExecutionService.Tests;

// 🔴 FR-10, FR-19, ADR-0067 決定1〜3・6, #1339, IADR-0539: 約定追跡の常駐が巡回ごとに「判断由来の決済の指値の未約定時の取消」を呼び、
// 確認できた取消を OrderCancelled として発行する（リスク管理の台帳・注文アクティビティ射影＝相場操縦の検知の母集団へ届く）。
// 取り消した決済は、次の巡回で約定追跡が終端（Cancelled）を確かめてから OrderExecuted として解放される（IADR-0536 決定3 を変えない）。
// 作業仕様書 20261016_1339_decision-close-unfilled-cancel §受け入れ基準 A6・A7。
public class DecisionCloseUnfilledCancelPollingTests
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

        public List<string> Cancelled { get; } = [];

        // 取消は「進行中」（moomoo の Cancelling_All → まだ有効）を返し、終端は次の照会で返す（実 OpenD と同じ順）。
        public bool ConfirmOnNextQuery { get; set; }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult(Orders.TryGetValue(orderId, out var o) ? o : null);

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new InvalidOperationException("置き直し・成行への切り替えはしない（ADR-0067 決定1）");

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            Cancelled.Add(orderId);
            if (!ConfirmOnNextQuery && Orders[orderId] is { } o)
                Orders[orderId] = o with { Status = OrderStatus.Cancelled, CompletedAt = Now };
            return Task.CompletedTask;
        }
    }

    private static readonly OrderIntent CloseIntent =
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate, 13, 347.93m,
            PositionEffect.Close);

    private static Task<IHost> BuildHostAsync(
        Broker broker, IExecutedOrderStore store, IClock clock, DecisionCloseUnfilledCancelOptions options) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(clock);
                opts.Services.AddSingleton<IBrokerAdapter>(broker);
                opts.Services.AddSingleton(store);
                opts.Services.AddSingleton<IOrderLifecycleStore, InMemoryOrderLifecycleStore>();
                opts.Services.AddScoped<OrderFillPoller>();
                opts.Services.AddScoped<OrderAmendmentService>();
                opts.Services.AddSingleton(Options.Create(options));
                opts.Services.AddScoped<DecisionCloseUnfilledCanceller>();
                opts.Services.AddSingleton<IOrderExpenseSource, UnsuppliedOrderExpenseSource>();
                opts.Services.AddScoped<TradeExpenseRecordingService>();
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static OrderFillPollingService BuildService(IHost host) =>
        new(host.Services.GetRequiredService<IServiceScopeFactory>(),
            host.Services.GetRequiredService<IWolverineRuntime>(),
            Options.Create(new FillPollingOptions()),
            NullLogger<OrderFillPollingService>.Instance);

    // T-10-2676: 有効なら、発注から N 分たった判断由来の決済を取り消し、確認できた取消を OrderCancelled で出す。
    // 確認できない（取消の進行中）なら出さず、次の巡回で約定追跡が終端を確かめてから OrderExecuted(Cancelled) で解放する。
    // 無効（既定）なら常駐は何も取り消さない。
    [Fact]
    public async Task T_10_2676_常駐が巡回ごとに取り消し確認できた取消だけを発行する()
    {
        var store = new InMemoryExecutedOrderStore();
        var broker = new Broker { ConfirmOnNextQuery = true };
        var clock = new MutableClock();
        var decisionId = Guid.NewGuid();
        store.Save(new ExecutionRecord(decisionId, "ORD-1", "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            PositionEffect.Close, 13, 347.93m, 0, 0m, OrderStatus.Accepted, 0m, Now.AddMinutes(-16),
            ApprovalOrigin: OrderApprovalOrigin.TradeDecision));
        broker.Orders["ORD-1"] = new BrokerOrder("ORD-1", CloseIntent, OrderStatus.Accepted, 0, 0m, default, null);

        using var host = await BuildHostAsync(
            broker, store, clock, new DecisionCloseUnfilledCancelOptions { Enabled = true });
        var service = BuildService(host);
        Func<IMessageContext, Task> poll = async _ => await service.PollOnceAsync(CancellationToken.None);

        // 1 回目: 取消を送る。まだ生きている（進行中）ので OrderCancelled も OrderExecuted も出さない。
        var first = await host.TrackActivityForTest().ExecuteAndWaitAsync(poll);
        broker.Cancelled.Should().Equal(["ORD-1"]);
        first.Sent.MessagesOf<OrderCancelled>().Should().BeEmpty("取り消せたと確認できない取消は発行しない（在庫の押さえを解かない）");
        first.Sent.MessagesOf<OrderExecuted>().Should().BeEmpty();
        store.FindByDecisionId(decisionId)!.Status.Should().Be(OrderStatus.Accepted, "解放は終端を確かめてから");

        // ブローカーが取消を終える。2 回目: 約定追跡が終端を確かめて解放する。取消は二度送らない。
        broker.Orders["ORD-1"] = broker.Orders["ORD-1"]! with { Status = OrderStatus.Cancelled, CompletedAt = Now };
        clock.UtcNow = Now.AddSeconds(30);
        var second = await host.TrackActivityForTest().ExecuteAndWaitAsync(poll);
        second.Sent.MessagesOf<OrderExecuted>().Should().ContainSingle(m =>
            m.DecisionId == decisionId && m.Status == OrderStatus.Cancelled);
        broker.Cancelled.Should().Equal(["ORD-1"]);

        await host.StopAsync();
    }

    [Fact]
    public async Task T_10_2676_確認できた取消はOrderCancelledとして発行し無効なら取り消さない()
    {
        foreach (var enabled in new[] { true, false })
        {
            var store = new InMemoryExecutedOrderStore();
            var broker = new Broker();
            var decisionId = Guid.NewGuid();
            store.Save(new ExecutionRecord(decisionId, "ORD-1", "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                PositionEffect.Close, 13, 347.93m, 0, 0m, OrderStatus.Accepted, 0m, Now.AddMinutes(-16),
                ApprovalOrigin: OrderApprovalOrigin.TradeDecision));
            broker.Orders["ORD-1"] = new BrokerOrder("ORD-1", CloseIntent, OrderStatus.Accepted, 0, 0m, default, null);

            using var host = await BuildHostAsync(
                broker, store, new MutableClock(), new DecisionCloseUnfilledCancelOptions { Enabled = enabled });
            var service = BuildService(host);

            Func<IMessageContext, Task> poll = async _ => await service.PollOnceAsync(CancellationToken.None);
            var tracked = await host.TrackActivityForTest().ExecuteAndWaitAsync(poll);

            if (enabled)
            {
                broker.Cancelled.Should().Equal(["ORD-1"]);
                tracked.Sent.MessagesOf<OrderCancelled>().Should().ContainSingle(m =>
                    m.DecisionId == decisionId && m.OrderId == "ORD-1"
                    && m.Reason.StartsWith(DecisionCloseUnfilledCanceller.ReasonPrefix, StringComparison.Ordinal));
            }
            else
            {
                broker.Cancelled.Should().BeEmpty("ADR-0067 決定2: 既定は無効");
                tracked.Sent.MessagesOf<OrderCancelled>().Should().BeEmpty();
            }

            await host.StopAsync();
        }
    }

    // T-10-2682: 回数の上限で取り消さなかった注文の記憶は、その注文の市場の現地取引日が替わったら消える（上限は日ごとに数え直す）。
    [Fact]
    public async Task T_10_2682_上限で取り消さなかった注文の記憶は取引日が替わると消える()
    {
        var store = new InMemoryExecutedOrderStore();
        var broker = new Broker();
        var clock = new MutableClock();
        foreach (var (id, minutes) in new[] { ("A-1", -40), ("A-2", -30) })
        {
            store.Save(new ExecutionRecord(Guid.NewGuid(), id, "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                PositionEffect.Close, 13, 347.93m, 0, 0m, OrderStatus.Accepted, 0m, Now.AddMinutes(minutes),
                ApprovalOrigin: OrderApprovalOrigin.TradeDecision));
            broker.Orders[id] = new BrokerOrder(id, CloseIntent, OrderStatus.Accepted, 0, 0m, default, null);
        }

        using var host = await BuildHostAsync(broker, store, clock, new DecisionCloseUnfilledCancelOptions { Enabled = true });
        var service = BuildService(host);

        await service.PollOnceAsync(CancellationToken.None); // A-1 を取り消し、A-2 は上限で取り消さない
        broker.Cancelled.Should().Equal(["A-1"]);
        service.CappedOrderIds.Should().Equal(["A-2"]);

        // 同じ取引日（米国東部 10-14 23:59 = 10-15 03:59Z）のうちは覚えている。
        clock.UtcNow = new DateTimeOffset(2026, 10, 15, 3, 59, 0, TimeSpan.Zero);
        await service.PollOnceAsync(CancellationToken.None);
        service.CappedOrderIds.Should().Equal(["A-2"]);

        // 取引日が替わる（10-15 04:00Z = 米国東部 10-15 00:00）と消える。
        clock.UtcNow = new DateTimeOffset(2026, 10, 15, 4, 0, 0, TimeSpan.Zero);
        await service.PollOnceAsync(CancellationToken.None);
        service.CappedOrderIds.Should().BeEmpty();
        broker.Cancelled.Should().Equal(["A-1"], "前の取引日の A-2 は候補外（取り消さない）");

        await host.StopAsync();
    }

    // T-10-2675（起動）: 本番の組み立て（Program.cs）で、有効時の N ≦ 検知の窓は起動で止まる。無効なら同じ値でも起動する。
    // NFR, #1437, IADR-0571: 起動の検証の例外は StartupValidationProbe で読む。factory.Services の例外の型は、アプリ側の
    // ホスト破棄と試験側の問い合わせの走る順で OptionsValidationException にも ObjectDisposedException にもなる（CI で後者が出た）。
    [Theory]
    [InlineData("true", "5", false)]
    [InlineData("true", "6", true)]
    [InlineData("false", "5", true)]
    public void T_10_2675_有効時のNが検知の窓以下なら起動で止まる(string enabled, string minutes, bool starts)
    {
        using var factory = new ExecutionWorkerWebApplicationFactory();
        DecisionCloseUnfilledCancelOptions? resolved = null;

        var outcome = factory.ProbeStartupValidation(
            b => b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DecisionCloseUnfilledCancel:Enabled"] = enabled,
                ["DecisionCloseUnfilledCancel:UnfilledMinutes"] = minutes,
            })),
            services => resolved = services.GetRequiredService<IOptions<DecisionCloseUnfilledCancelOptions>>().Value);

        outcome.ValidatorPresent.Should().BeTrue("Program.cs は起動時の検証（ValidateOnStart）を登録する");
        if (starts)
        {
            outcome.ValidationFailure.Should().BeNull();
            outcome.StartFailure.Should().BeNull();
            resolved.Should().NotBeNull();
        }
        else
        {
            outcome.ValidationFailure.Should().BeOfType<OptionsValidationException>()
                .Which.Message.Should().Contain("相場操縦の検知の窓");
            outcome.StartFailure.Should().NotBeNull("起動の検証の失敗で起動は止まる");
        }
    }
}
