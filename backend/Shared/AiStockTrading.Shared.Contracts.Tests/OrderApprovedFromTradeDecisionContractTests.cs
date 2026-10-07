using System.Text.Json;
using System.Text.Json.Nodes;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// T-10-2337, FR-10, #1176, IADR-0495 決定4: 承認の由来の印（OrderApproved.FromTradeDecision）の後方互換の固定。
// EventBackwardCompatibilityTests はプロパティの型名しか見ない。本テストは「項目を持たない旧い OrderApproved 本文が
// false（判断を経ない承認）として読める」ことを押さえる（StopLossMethodContractTests の旧い本文の試験と同じ形）。
// 🔴 旧版の発行側が送った承認を true と読むと、判断由来でない決済の後に同日・同方向の新規建てを誤って止める。
public class OrderApprovedFromTradeDecisionContractTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 14, 0, 0, TimeSpan.Zero);

    private static OrderIntent CloseIntent() =>
        new("AMZN", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
            10, 220m, PositionEffect.Close, StopLossPrice: null, FxRateToBase: 1m);

    [Fact]
    public void T_10_2337_由来を明示しない承認は判断由来ではない()
    {
        new OrderApproved(Guid.NewGuid(), CloseIntent(), 10, T0).FromTradeDecision.Should().BeFalse();
    }

    // 🔴 旧版の発行側（本項目を持たない）が送った本文を新版の受け手（取引台帳）が読む経路。
    [Fact]
    public void T_10_2337_由来の印を持たない旧い承認本文はfalseとして読める()
    {
        var current = new OrderApproved(Guid.NewGuid(), CloseIntent(), 10, T0, FromTradeDecision: true);
        var node = JsonNode.Parse(JsonSerializer.Serialize(current))!.AsObject();
        node.Remove(nameof(OrderApproved.FromTradeDecision)).Should().BeTrue("旧版の本文には本項目が無い");

        var restored = JsonSerializer.Deserialize<OrderApproved>(node.ToJsonString())!;

        restored.FromTradeDecision.Should().BeFalse();
        restored.DecisionId.Should().Be(current.DecisionId);
        restored.ApprovedQuantity.Should().Be(10);
    }

    [Fact]
    public void T_10_2337_判断由来の承認はJSONを往復しても印を保つ()
    {
        var approved = new OrderApproved(Guid.NewGuid(), CloseIntent(), 10, T0, FromTradeDecision: true);

        JsonSerializer.Deserialize<OrderApproved>(JsonSerializer.Serialize(approved))!
            .FromTradeDecision.Should().BeTrue();
    }
}
