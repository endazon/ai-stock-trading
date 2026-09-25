using System.Reflection;
using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution.RecordTradeExpenses;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// 🔴 T-10-1082, FR-10, FR-06, FR-11, #1002, IADR-0429 決定1: 解決結果（StopLossMethodResolved）は報告のためだけの事実であり、
// **その発行の失敗が統制の発行（見送り・発注結果・保護逆指値）を止めてはならない**。ハンドラは例外を握って後続へ進む
// （経費の記録と同じ形）。投げる形（同期の例外・失敗した ValueTask）の両方で、見送りの回と発注の回を確かめる。
public class OrderApprovedMethodResolvedPublishFailureTests
{
    // StopLossMethodResolved の発行だけが失敗し、それ以外は記録するバス。
    public class FailingResolvedBus : DispatchProxy
    {
        public List<object> Published { get; } = [];

        public bool ThrowSynchronously { get; set; }

        public int ResolvedAttempts { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IMessageBus.PublishAsync))
                throw new NotSupportedException(targetMethod?.Name);

            var message = args![0]!;
            if (message is StopLossMethodResolved)
            {
                ResolvedAttempts++;
                var failure = new InvalidOperationException("RabbitMQ へ送れません（テスト）");
                if (ThrowSynchronously)
                    throw failure;
                return ValueTask.FromException(failure);
            }

            Published.Add(message);
            return ValueTask.CompletedTask;
        }
    }

    private static OrderApprovedHandler NewHandler()
    {
        var store = new InMemoryExecutedOrderStore();
        var service = new AppSvc(
            new PaperBrokerAdapter(), store, new InMemoryOrderReservationStore(), new SystemClock());
        return new OrderApprovedHandler(
            service,
            new TradeExpenseRecordingService(new UnsuppliedOrderExpenseSource(), store),
            new BusinessMetrics(),
            NullLogger<OrderApprovedHandler>.Instance);
    }

    private static OrderApproved Approved(StopLossExecutionMethod method) => new(
        Guid.NewGuid(),
        new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 1_000m,
            StopLossPrice: 950m),
        10,
        DateTimeOffset.UtcNow,
        StopLossMethod: method);

    // 内蔵 paper（SIMULATE ではない）へ S2 → 拒否（見送り）。解決結果の発行が失敗しても見送りは発行される。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T_10_1082_解決結果の発行が失敗しても見送りは発行される(bool throwSynchronously)
    {
        var bus = DispatchProxy.Create<IMessageBus, FailingResolvedBus>();
        var proxy = (FailingResolvedBus)(object)bus;
        proxy.ThrowSynchronously = throwSynchronously;
        var approved = Approved(StopLossExecutionMethod.NoProtectiveStop);

        var handle = () => NewHandler().Handle(approved, bus, CancellationToken.None);

        await handle.Should().NotThrowAsync("報告のための発行の失敗で承認を再配送させない");
        proxy.ResolvedAttempts.Should().Be(1, "前提: 解決結果の発行は試みて、失敗している");
        proxy.Published.OfType<OrderDispatchForgone>().Should().ContainSingle()
            .Which.Reason.Should().Be(OrderDispatchForgoneReason.StopLossMethodNotPermitted);
    }

    // S0 → 発注。解決結果の発行が失敗しても、発注結果と保護逆指値は発行される。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T_10_1082_解決結果の発行が失敗しても発注結果と保護逆指値は発行される(bool throwSynchronously)
    {
        var bus = DispatchProxy.Create<IMessageBus, FailingResolvedBus>();
        var proxy = (FailingResolvedBus)(object)bus;
        proxy.ThrowSynchronously = throwSynchronously;
        var approved = Approved(StopLossExecutionMethod.BrokerStopOrder);

        await NewHandler().Handle(approved, bus, CancellationToken.None);

        proxy.ResolvedAttempts.Should().Be(1);
        proxy.Published.OfType<OrderExecuted>().Should().ContainSingle().Which.DecisionId.Should().Be(approved.DecisionId);
        proxy.Published.OfType<ProtectiveStopPlaced>().Should().ContainSingle().Which.EntryDecisionId.Should().Be(approved.DecisionId);
    }
}
