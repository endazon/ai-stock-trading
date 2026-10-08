using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Infrastructure.Persistence;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// FR-10, UC-06, ADR-0050 決定1, #1222, IADR-0515 決定2: 承認の出どころ（OrderApproved.Origin）を、発注執行は発注結果の記録
// （executed_orders.ApprovalOrigin）へ写す。S1 の決済の前の取消が、利用者の手仕舞い・維持率割れの自動縮小を取り消さず差し引くために読む。
// Unknown（旧いメッセージ）は null（分からない）で書く。列を足す前の行も null で読まれる。
public class ApprovalOriginPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 15, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private static OrderIntent Entry() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 100m, PositionEffect.Open,
            StopLossPrice: 98m);

    private static ExecutionRecord Record(string orderId, OrderApprovalOrigin? origin) =>
        new(Guid.NewGuid(), orderId, "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, PositionEffect.Close,
            10, 100m, 0, 0m, OrderStatus.Accepted, 0m, Now, ApprovalOrigin: origin);

    // T-10-2441: 本番の DB 実装（EF）とインメモリで、出どころを保存して読み戻す（FindPendingCloses・FindByDecisionId・GetAll）。
    // 出どころを渡さない記録（保護の機構・突合・列を足す前）は null。約定の更新（UpdateOutcome）で出どころは消えない。
    [Theory]
    [InlineData("ef")]
    [InlineData("inmemory")]
    public void T_10_2441_発注の記録は出どころを保存して読み戻し約定の更新で消えない(string kind)
    {
        var options = new DbContextOptionsBuilder<OrderExecutionDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var memory = new InMemoryExecutedOrderStore();
        Func<OrderExecutionService.Features.OrderExecution.IExecutedOrderStore> open = kind == "ef"
            ? () => new EfExecutedOrderStore(new OrderExecutionDbContext(options))
            : () => memory;

        var owner = Record("owner", OrderApprovalOrigin.OwnerClose);
        var reduction = Record("reduction", OrderApprovalOrigin.MaintenanceMarginReduction);
        var decision = Record("decision", OrderApprovalOrigin.TradeDecision);
        var unknown = Record("unknown", origin: null);
        foreach (var r in new[] { owner, reduction, decision, unknown })
            open().Save(r);
        open().UpdateOutcome("owner", OrderStatus.PartiallyFilled, 4, 100m, 0m, Now.AddSeconds(5)).Should().BeTrue();

        var pending = open().FindPendingCloses("AAPL", Market.UnitedStates, TradeSide.Sell).ToDictionary(r => r.OrderId);
        pending["owner"].ApprovalOrigin.Should().Be(OrderApprovalOrigin.OwnerClose);
        pending["owner"].FilledQuantity.Should().Be(4);
        pending["reduction"].ApprovalOrigin.Should().Be(OrderApprovalOrigin.MaintenanceMarginReduction);
        pending["decision"].ApprovalOrigin.Should().Be(OrderApprovalOrigin.TradeDecision);
        pending["unknown"].ApprovalOrigin.Should().BeNull("渡さない記録は分からない（null）");
        open().FindByDecisionId(owner.DecisionId)!.ApprovalOrigin.Should().Be(OrderApprovalOrigin.OwnerClose);
        open().GetAll().Single(r => r.OrderId == "reduction").ApprovalOrigin
            .Should().Be(OrderApprovalOrigin.MaintenanceMarginReduction);
    }

    // T-10-2442: 承認の経路の発注は、承認の出どころを発注結果の記録へ写す。Unknown（旧いメッセージ・書き手の渡し忘れ）は null で書く。
    [Theory]
    [InlineData(OrderApprovalOrigin.TradeDecision, OrderApprovalOrigin.TradeDecision)]
    [InlineData(OrderApprovalOrigin.OwnerClose, OrderApprovalOrigin.OwnerClose)]
    [InlineData(OrderApprovalOrigin.MaintenanceMarginReduction, OrderApprovalOrigin.MaintenanceMarginReduction)]
    [InlineData(OrderApprovalOrigin.Unknown, null)]
    public async Task T_10_2442_発注は承認の出どころを発注結果の記録に写す(
        OrderApprovalOrigin origin, OrderApprovalOrigin? expected)
    {
        var store = new InMemoryExecutedOrderStore();
        var service = new AppSvc(new PaperBrokerAdapter(), store, new InMemoryOrderReservationStore(), new FakeClock());
        var approved = new OrderApproved(Guid.NewGuid(), Entry(), 10, Now, Origin: origin);

        await service.ExecuteAsync(approved);

        store.FindByDecisionId(approved.DecisionId)!.ApprovalOrigin.Should().Be(expected);
    }
}
