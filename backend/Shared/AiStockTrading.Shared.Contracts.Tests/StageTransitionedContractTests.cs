using System.Text.Json;
using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// FR-20, FR-11, FR-14, UC-06, #868, IADR-0240 決定11, IADR-0383: StageTransitioned へ末尾に足した
// AuthorizedBy（任意）が**後方互換の追加**であることを固定する。発行側（リスク管理）と購読側（監査）は
// 別々に配備されるため、旧形式（AuthorizedBy 無し）の JSON を新しい購読側が読める必要がある
// （IADR-0079 / IADR-0134 決定2 の規律。ReportConfirmed と同型）。
//
// テスト ID: T-140（`docs/tests/FR-20_staged-gates-tests.md`）。
public class StageTransitionedContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void 旧形式の_JSON_は_AuthorizedBy_が_null_として読める()
    {
        const string legacy =
            """
            {"sequence":1,"fromStage":0,"toStage":1,"kind":"Promotion","approvedBy":"owner",
             "reason":"利用者承認による昇格","occurredAt":"2026-09-23T04:36:00+00:00",
             "stage1MinimumTradeCount":100,"stage1BelowStatisticalBasis":false}
            """;

        var e = JsonSerializer.Deserialize<StageTransitioned>(legacy, Web)!;

        e.ApprovedBy.Should().Be("owner");
        e.AuthorizedBy.Should().BeNull();
    }

    [Fact]
    public void 代理承認は_ApprovedBy_と_AuthorizedBy_の両方が往復する()
    {
        var original = new StageTransitioned(
            1, 0, 1, "Promotion", "developer", "利用者承認による昇格",
            new DateTimeOffset(2026, 9, 23, 4, 36, 0, TimeSpan.Zero), 100, false,
            AuthorizedBy: "ai-stock-trading-owner");

        var roundTripped = JsonSerializer.Deserialize<StageTransitioned>(JsonSerializer.Serialize(original, Web), Web);

        roundTripped.Should().Be(original);
    }

    [Fact]
    public void 既存の位置引数の並びは変わらない_AuthorizedBy_は末尾の任意引数である()
    {
        var parameters = typeof(StageTransitioned).GetConstructors()
            .Single(c => c.GetParameters().Length > 1).GetParameters();

        parameters.Select(p => p.Name).Should().Equal(
            "Sequence", "FromStage", "ToStage", "Kind", "ApprovedBy", "Reason", "OccurredAt",
            "Stage1MinimumTradeCount", "Stage1BelowStatisticalBasis", "AuthorizedBy");
        parameters[^1].HasDefaultValue.Should().BeTrue();
        parameters[^1].DefaultValue.Should().BeNull();
    }
}
