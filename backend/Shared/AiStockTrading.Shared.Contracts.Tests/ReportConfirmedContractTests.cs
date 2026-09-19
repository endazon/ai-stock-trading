using System.Text.Json;
using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// FR-07, FR-09, UC-03, IADR-0240 決定11, #774: ReportConfirmed へ末尾に足した AuthorizedBy（任意）が
// **後方互換の追加**であることを固定する。発行側（報告書）と購読側（通知・監査）は別々に配備されるため、
// 旧形式（AuthorizedBy 無し）の JSON を新しい購読側が読める必要がある。
public class ReportConfirmedContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void 旧形式の_JSON_は_AuthorizedBy_が_null_として読める()
    {
        const string legacy =
            """{"periodKey":"daily-2026-09-10","kind":"Daily","actor":"owner","assumptionsVersion":1,"confirmedAt":"2026-09-11T04:36:00+00:00"}""";

        var e = JsonSerializer.Deserialize<ReportConfirmed>(legacy, Web)!;

        e.Actor.Should().Be("owner");
        e.AuthorizedBy.Should().BeNull();
    }

    [Fact]
    public void 代理確定は_Actor_と_AuthorizedBy_の両方が往復する()
    {
        var original = new ReportConfirmed(
            "daily-2026-09-10", "Daily", "developer", 1,
            new DateTimeOffset(2026, 9, 11, 4, 36, 0, TimeSpan.Zero), AuthorizedBy: "ai-stock-trading-owner");

        var roundTripped = JsonSerializer.Deserialize<ReportConfirmed>(JsonSerializer.Serialize(original, Web), Web);

        roundTripped.Should().Be(original);
    }

    [Fact]
    public void 既存の位置引数の並びは変わらない_AuthorizedBy_は末尾の任意引数である()
    {
        var parameters = typeof(ReportConfirmed).GetConstructors()
            .Single(c => c.GetParameters().Length > 1).GetParameters();

        parameters.Select(p => p.Name).Should().Equal(
            "PeriodKey", "Kind", "Actor", "AssumptionsVersion", "ConfirmedAt", "AuthorizedBy");
        parameters[^1].HasDefaultValue.Should().BeTrue();
        parameters[^1].DefaultValue.Should().BeNull();
    }
}
