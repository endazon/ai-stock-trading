using System.Text.Json;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// FR-04, FR-11, ADR-0068 決定4, #1373, IADR-0548 決定5: 判断の記録（TradeDecisionMade / TradeDecisionHeld）の末尾に足した
// Policy（参照した確定済み日報の方針の期間キーと版番号）が**後方互換の追加**であることを固定する。
// 発行側（取引判断）と購読側（監査・市場監視・リスク管理）は別々に配備されるため、旧形式の JSON を新しい購読側が読める必要がある。
public class DecisionPolicyReferenceContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void 旧形式の判断後の見送りは_Policy_が_null_として読める()
    {
        const string legacy =
            """{"eventId":"7f0c2b9e-1d1a-4e43-9a59-0d8e2d4d2a10","symbol":"AAPL","market":1,"price":212.5,"reason":"LlmHold","decidedAt":"2026-10-16T15:13:00+00:00"}""";

        var e = JsonSerializer.Deserialize<TradeDecisionHeld>(legacy, Web)!;

        e.Symbol.Should().Be("AAPL");
        e.Policy.Should().BeNull();
    }

    [Fact]
    public void 期間キーと版番号は判断後の見送りで往復する()
    {
        var original = new TradeDecisionHeld(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, 212.5m, "LlmHold",
            new DateTimeOffset(2026, 10, 16, 15, 13, 0, TimeSpan.Zero),
            Policy: new DailyPolicyReference("daily-2026-10-17", 4));

        var roundTripped = JsonSerializer.Deserialize<TradeDecisionHeld>(JsonSerializer.Serialize(original, Web), Web)!;

        roundTripped.Policy.Should().Be(new DailyPolicyReference("daily-2026-10-17", 4));
    }

    [Theory]
    [InlineData(typeof(TradeDecisionMade))]
    [InlineData(typeof(TradeDecisionHeld))]
    public void Policy_は末尾の任意引数である_既存の位置引数の並びを変えない(Type eventType)
    {
        var parameters = eventType.GetConstructors().Single(c => c.GetParameters().Length > 1).GetParameters();

        parameters[^1].Name.Should().Be("Policy");
        parameters[^1].ParameterType.Should().Be<DailyPolicyReference>();
        parameters[^1].HasDefaultValue.Should().BeTrue();
        parameters[^1].DefaultValue.Should().BeNull();
        parameters[^2].Name.Should().Be("References");
    }
}
