using System.Text.Json;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// 🔴 T-10-1006, FR-10, FR-09, #879, IADR-0424 決定1: OrderDispatchForgone へ末尾に足した Protection（任意）が
// **後方互換の追加**であることを固定する。発行側（発注執行）と購読側（通知・監査・リスク管理）は別々に配備されるため、
// 旧形式（Protection 無し）の JSON を新しい購読側が読める必要がある（IADR-0079 / IADR-0134 決定2。PositionDriftAdopted.AuthorizedBy と同型）。
public class OrderDispatchForgoneProtectionContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void 旧形式の_JSON_は_Protection_が_null_として読める()
    {
        const string legacy =
            """
            {"decisionId":"5f2b1a64-9f6e-4b69-9d6f-0d6c5a0b7e11",
             "intent":{"symbol":"AAPL","market":1,"side":1,"productType":0,"mode":2,"quantity":300,"price":100,"positionEffect":1},
             "reason":5,"occurredAt":"2026-09-25T06:00:00+00:00"}
            """;

        var e = JsonSerializer.Deserialize<OrderDispatchForgone>(legacy, Web)!;

        e.Reason.Should().Be(OrderDispatchForgoneReason.BrokerPositionsIndeterminate);
        e.Intent.Quantity.Should().Be(300);
        e.Protection.Should().BeNull("旧い送り手は判別を試みていない＝受け手は「分からない」と読む");
    }

    [Fact]
    public void 保護の記録は往復する()
    {
        var original = new OrderDispatchForgone(
            Guid.NewGuid(),
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
                300, 100m, PositionEffect.Close),
            OrderDispatchForgoneReason.BrokerPositionsIndeterminate,
            new DateTimeOffset(2026, 9, 25, 6, 0, 0, TimeSpan.Zero),
            new ForgoneCloseProtection(ForgoneCloseProtectionStatus.Recorded, 130, 50));

        var roundTripped = JsonSerializer.Deserialize<OrderDispatchForgone>(JsonSerializer.Serialize(original, Web), Web);

        roundTripped.Should().Be(original);
    }

    [Fact]
    public void 既存の位置引数の並びは変わらない_Protection_は末尾の任意引数である()
    {
        var parameters = typeof(OrderDispatchForgone).GetConstructors()
            .Single(c => c.GetParameters().Length > 1).GetParameters();

        parameters.Select(p => p.Name).Should().Equal("DecisionId", "Intent", "Reason", "OccurredAt", "Protection");
        parameters[^1].HasDefaultValue.Should().BeTrue();
        parameters[^1].DefaultValue.Should().BeNull();
    }

    // 🔴 序数 0 は Unknown（既定値へ落ちた値は「分からない」側へ倒れる）。序数は監査 payload の整数として往来するため固定する。
    [Fact]
    public void 保護の読みの序数は_Unknown_が0で末尾へ追加する()
    {
        ((int)ForgoneCloseProtectionStatus.Unknown).Should().Be(0);
        ((int)ForgoneCloseProtectionStatus.NoneRecorded).Should().Be(1);
        ((int)ForgoneCloseProtectionStatus.Recorded).Should().Be(2);
        Enum.GetValues<ForgoneCloseProtectionStatus>().Should().HaveCount(3, "増えたら受け手の書き分けを見直す");
        default(ForgoneCloseProtectionStatus).Should().Be(ForgoneCloseProtectionStatus.Unknown);
    }
}
