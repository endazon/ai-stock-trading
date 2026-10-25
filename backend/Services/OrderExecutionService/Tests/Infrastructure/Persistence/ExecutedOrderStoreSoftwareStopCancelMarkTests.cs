using Microsoft.EntityFrameworkCore;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-2917, FR-10, FR-09, UC-06, #1395, IADR-0560: 「S1 が取消を求めた」印の書き込みと読み直しの意味論を EF とインメモリで同一に固定する。
public class ExecutedOrderStoreSoftwareStopCancelMarkTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 6, 30, 0, TimeSpan.Zero);

    public static TheoryData<string> Implementations() => ["ef", "inmemory"];

    private static ExecutionRecord Close(string orderId, OrderStatus status = OrderStatus.Accepted) =>
        new(Guid.NewGuid(), orderId, "SMCI", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, PositionEffect.Close,
            7165, 32.56m, status == OrderStatus.Filled ? 7165 : 0, 0m, status, 0m, Now.AddMinutes(-10),
            ApprovalOrigin: OrderApprovalOrigin.TradeDecision);

    [Theory]
    [MemberData(nameof(Implementations))]
    public void T_10_2917_印は非終端にだけ書け上書きせず保存先から読み直せる(string kind)
    {
        var store = Stores(kind);
        store().Save(Close("live"));
        store().Save(Close("partial", OrderStatus.PartiallyFilled));
        store().Save(Close("filled", OrderStatus.Filled));

        // 巡回が先に記録を読んだ文脈（EF は追跡した行を持つ）。
        var poller = store();
        poller.FindPendingSince(Now.AddHours(-1), 100).Select(r => r.OrderId).Should().BeEquivalentTo(["live", "partial"]);

        store().MarkSoftwareStopCancelRequested("live", Now).Should().BeTrue();
        store().MarkSoftwareStopCancelRequested("live", Now.AddMinutes(1)).Should().BeTrue("巡回ごとに取り消し直す。印は最初の時刻のまま");
        store().MarkSoftwareStopCancelRequested("partial", Now).Should().BeTrue();
        store().MarkSoftwareStopCancelRequested("filled", Now).Should().BeFalse("終端");
        store().MarkSoftwareStopCancelRequested("missing", Now).Should().BeFalse("記録が無い");

        poller.FindSoftwareStopCancelRequestedAt("live").Should().Be(Now, "先に読んだ文脈からも保存先の値を読む");
        poller.FindSoftwareStopCancelRequestedAt("filled").Should().BeNull();
        poller.FindSoftwareStopCancelRequestedAt("missing").Should().BeNull();
        store().GetAll().Single(r => r.OrderId == "live").SoftwareStopCancelRequestedAt.Should().Be(Now);

        // 取消が失敗したときに戻す（非終端だけ）。T-10-2918。
        store().ClearSoftwareStopCancelRequested("partial").Should().BeTrue();
        poller.FindSoftwareStopCancelRequestedAt("partial").Should().BeNull();
        store().ClearSoftwareStopCancelRequested("filled").Should().BeFalse("終端");
        store().ClearSoftwareStopCancelRequested("missing").Should().BeFalse("記録が無い");

        // 系の未約定取消の印（回数の上限に数える）とは別の列。
        store().GetAll().Single(r => r.OrderId == "live").UnfilledCancelRequestedAt.Should().BeNull();
        store().FindUnfilledDecisionCloses(Now, 100).Select(r => r.OrderId).Should().BeEquivalentTo(["live", "partial"],
            "S1 の印は N 分未約定の取消の対象から外さない");
    }

    private static Func<IExecutedOrderStore> Stores(string kind)
    {
        if (kind != "ef")
        {
            var shared = new InMemoryExecutedOrderStore();
            return () => shared;
        }

        var dbName = Guid.NewGuid().ToString();
        return () => new EfExecutedOrderStore(new OrderExecutionDbContext(
            new DbContextOptionsBuilder<OrderExecutionDbContext>().UseInMemoryDatabase(dbName).Options));
    }
}
