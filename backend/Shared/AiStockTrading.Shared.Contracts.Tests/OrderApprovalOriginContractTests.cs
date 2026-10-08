using System.Text.Json;
using System.Text.Json.Nodes;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// T-10-2443, FR-10, UC-06, ADR-0050 決定1, #1222, IADR-0515 決定1: 承認の出どころ（OrderApproved.Origin）の契約の固定。
//   - 序数（発注執行が executed_orders.ApprovalOrigin へ整数で永続化する）。間へ挿入すると過去の記録の意味が変わる。
//   - 🔴 本項目を持たない旧い承認本文は Unknown（分からない）として読める。S1 は Unknown を判断の手仕舞いと同じく取り消す側へ倒す
//     ——旧い本文を OwnerClose と読むと、判断の手仕舞いを取り消さずに S1 が据え置かれ得る（損切りを止める側）。
public class OrderApprovalOriginContractTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 14, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<OrderApprovalOrigin, int> Ordinals = new Dictionary<OrderApprovalOrigin, int>
    {
        [OrderApprovalOrigin.Unknown] = 0,
        [OrderApprovalOrigin.TradeDecision] = 1,
        [OrderApprovalOrigin.OwnerClose] = 2,
        [OrderApprovalOrigin.MaintenanceMarginReduction] = 3,
    };

    private static OrderIntent CloseIntent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
            10, 220m, PositionEffect.Close, StopLossPrice: null, FxRateToBase: 1m);

    [Fact]
    public void T_10_2443_出どころの序数は固定され追加は末尾に限る()
    {
        Enum.GetValues<OrderApprovalOrigin>().Should().BeEquivalentTo(Ordinals.Keys);
        foreach (var (origin, ordinal) in Ordinals)
            ((int)origin).Should().Be(ordinal, $"{origin} の序数");
    }

    [Fact]
    public void T_10_2443_出どころを明示しない承認は分からないである()
    {
        new OrderApproved(Guid.NewGuid(), CloseIntent(), 10, T0).Origin.Should().Be(OrderApprovalOrigin.Unknown);
    }

    // 🔴 旧版の発行側（本項目を持たない）が送った本文を新版の受け手（発注執行）が読む経路。
    [Fact]
    public void T_10_2443_出どころを持たない旧い承認本文は分からないとして読める()
    {
        var current = new OrderApproved(Guid.NewGuid(), CloseIntent(), 10, T0, Origin: OrderApprovalOrigin.OwnerClose);
        var node = JsonNode.Parse(JsonSerializer.Serialize(current))!.AsObject();
        node.Remove(nameof(OrderApproved.Origin)).Should().BeTrue("旧版の本文には本項目が無い");

        var restored = JsonSerializer.Deserialize<OrderApproved>(node.ToJsonString())!;

        restored.Origin.Should().Be(OrderApprovalOrigin.Unknown);
        restored.DecisionId.Should().Be(current.DecisionId);
    }

    [Theory]
    [InlineData(OrderApprovalOrigin.TradeDecision)]
    [InlineData(OrderApprovalOrigin.OwnerClose)]
    [InlineData(OrderApprovalOrigin.MaintenanceMarginReduction)]
    public void T_10_2443_出どころはJSONを往復しても保たれる(OrderApprovalOrigin origin)
    {
        var approved = new OrderApproved(Guid.NewGuid(), CloseIntent(), 10, T0, Origin: origin);

        JsonSerializer.Deserialize<OrderApproved>(JsonSerializer.Serialize(approved))!.Origin.Should().Be(origin);
    }
}
