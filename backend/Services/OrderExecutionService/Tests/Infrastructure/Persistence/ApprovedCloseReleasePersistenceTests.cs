using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Infrastructure.Migrations;
using OrderExecutionService.Infrastructure.Persistence;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-10, #1330, IADR-0534 決定1・決定2: 確定した保護の縮小を「承認済みの決済」と「外部要因」に分けるための 2 列の永続化。
// T-10-2639（受け入れ基準 10）。
public class ApprovedCloseReleasePersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 15, 19, 18, TimeSpan.Zero);

    private static ExecutionRecord Close(string orderId, OrderApprovalOrigin? origin, int filled, PositionEffect effect = PositionEffect.Close) =>
        new(Guid.NewGuid(), orderId, "MSFT", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, effect,
            456, 541.77m, filled, 541.77m, filled == 456 ? OrderStatus.Filled : OrderStatus.PartiallyFilled, 0m, Now,
            ApprovalOrigin: origin);

    [Theory]
    [InlineData("ef")]
    [InlineData("inmemory")]
    public void 帰属させた株数は単調に約定数量以下で進み問い合わせは未帰属の約定を持つ承認済みの決済だけを返す(string kind)
    {
        var options = new DbContextOptionsBuilder<OrderExecutionDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var memory = new InMemoryExecutedOrderStore();
        Func<IExecutedOrderStore> open = kind == "ef"
            ? () => new EfExecutedOrderStore(new OrderExecutionDbContext(options))
            : () => memory;

        var decision = Close("decision", OrderApprovalOrigin.TradeDecision, filled: 200);
        var owner = Close("owner", OrderApprovalOrigin.OwnerClose, filled: 456);
        var legacy = Close("legacy", origin: null, filled: 456);
        var unfilled = Close("unfilled", OrderApprovalOrigin.TradeDecision, filled: 0);
        var open_ = Close("open", OrderApprovalOrigin.TradeDecision, filled: 456, PositionEffect.Open) with { Side = TradeSide.Buy };
        foreach (var r in new[] { decision, owner, legacy, unfilled, open_ })
            open().Save(r);

        open().FindApprovedClosesWithUnreleasedFills("MSFT", Market.UnitedStates, TradeSide.Sell, 50)
            .Select(r => r.OrderId).Should().BeEquivalentTo(["decision", "owner"], "出どころの無い・約定の無い・建ての記録は入らない");

        open().MarkProtectionReleased("decision", 150).Should().BeTrue();
        open().MarkProtectionReleased("decision", 100).Should().BeFalse("単調（戻さない）");
        open().MarkProtectionReleased("decision", 999).Should().BeTrue("約定数量で切り詰める");
        open().FindByDecisionId(decision.DecisionId)!.ProtectionReleasedQuantity.Should().Be(200);
        open().MarkProtectionReleased("missing", 1).Should().BeFalse();

        // 約定が進めば再び未帰属の約定を持つ（部分約定の続き）。印の列だけを書き、状態・数量は上書きしない。
        open().UpdateOutcome("decision", OrderStatus.Filled, 456, 541.77m, 0m, Now.AddSeconds(30)).Should().BeTrue();
        var reloaded = open().FindByDecisionId(decision.DecisionId)!;
        reloaded.FilledQuantity.Should().Be(456);
        reloaded.ProtectionReleasedQuantity.Should().Be(200, "約定の更新で印は消えない");
        open().FindApprovedClosesWithUnreleasedFills("MSFT", Market.UnitedStates, TradeSide.Sell, 50)
            .Select(r => r.OrderId).Should().Contain("decision");

        open().MarkProtectionReleased("owner", 456).Should().BeTrue();
        open().FindApprovedClosesWithUnreleasedFills("MSFT", Market.UnitedStates, TradeSide.Sell, 50)
            .Select(r => r.OrderId).Should().NotContain("owner", "帰属させ終えた決済は返さない");
    }

    [Fact]
    public void 窓の前の端は保護記録に保存され読み戻せる()
    {
        var options = new DbContextOptionsBuilder<OrderExecutionDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var id = Guid.NewGuid();
        var stop = new ProtectiveStopOrder(
            id, ProtectiveStopIds.SoftwareStopId(id), string.Empty, "MSFT", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 1_000, 500m, 1m, 0, ProtectiveStopState.Active,
            Now, Now, StopLossExecutionMethod.SoftwareStop, RemainingProtected: 1_000,
            PendingExternalReduction: 456, ExternalReductionObservations: 1, ExternalReductionObservedAt: Now.AddSeconds(31));

        new EfProtectiveStopOrderStore(new OrderExecutionDbContext(options)).Save(stop);
        var store = new EfProtectiveStopOrderStore(new OrderExecutionDbContext(options));
        var loaded = store.Find(id)!;
        loaded.ExternalReductionObservedAt.Should().Be(Now.AddSeconds(31));

        store.TrySave(loaded with { ExternalReductionObservedAt = null }).Should().BeTrue();
        new EfProtectiveStopOrderStore(new OrderExecutionDbContext(options)).Find(id)!
            .ExternalReductionObservedAt.Should().BeNull();
    }

    // 🔴 移行で既存の決済の約定を吸収済みにする（過去の利確の約定で、これからの外部要因の減少を「承認済み」と誤って説明しない）。
    [Fact]
    public void 移行は列を足し既存の決済行の約定を吸収済みに揃える()
    {
        var migration = new AddApprovedCloseProtectionRelease();
        var ops = migration.UpOperations;

        ops.OfType<AddColumnOperation>().Select(o => (o.Table, o.Name, o.IsNullable)).Should().BeEquivalentTo(
            [("protective_stop_orders", "ExternalReductionObservedAt", true), ("executed_orders", "ProtectionReleasedQuantity", false)]);
        ops.OfType<SqlOperation>().Should().ContainSingle()
            .Which.Sql.Should().Contain("SET \"ProtectionReleasedQuantity\" = \"FilledQuantity\"")
            .And.Contain("\"PositionEffect\" = 1");
        ((int)PositionEffect.Close).Should().Be(1, "移行の SQL は決済の序数を直書きしている");
    }
}
