using System.Text.Json;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// 🔴 T-10-986, FR-10, FR-11, FR-14, UC-06, ADR-0041 決定 4, #871, IADR-0383, IADR-0423: PositionDriftAdopted へ末尾に足した
// AuthorizedBy（任意）が**後方互換の追加**であることを固定する。発行側（リスク管理）と購読側（監査・通知・発注執行）は
// 別々に配備されるため、旧形式（AuthorizedBy 無し）の JSON を新しい購読側が読める必要がある
// （IADR-0079 / IADR-0134 決定2 の規律。StageTransitioned・ReportConfirmed と同型）。
public class PositionDriftAdoptedContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void 旧形式の_JSON_は_AuthorizedBy_が_null_として読める()
    {
        const string legacy =
            """
            {"adoptionId":"5f2b1a64-9f6e-4b69-9d6f-0d6c5a0b7e11","symbol":"AAPL","market":1,
             "ledgerQuantityBefore":3381,"ledgerQuantityAfter":0,"brokerQuantity":0,
             "observedAt":"2026-09-19T01:00:00+00:00","costBasisPrice":0.5,"realizedPnlRecorded":false,
             "referencePrice":null,"estimatedPnlInBase":null,"actor":"owner",
             "reason":"証券会社のアプリで全株を売却した","adoptedAt":"2026-09-19T01:05:00+00:00"}
            """;

        var e = JsonSerializer.Deserialize<PositionDriftAdopted>(legacy, Web)!;

        e.Actor.Should().Be("owner");
        e.Reason.Should().Be("証券会社のアプリで全株を売却した");
        e.AuthorizedBy.Should().BeNull();
    }

    [Fact]
    public void 代理の取り込みは_Actor_と_AuthorizedBy_の両方が往復する()
    {
        var original = new PositionDriftAdopted(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, 3_381, 0, 0,
            new DateTimeOffset(2026, 9, 19, 1, 0, 0, TimeSpan.Zero), 0.5m, false, null, null,
            "developer", "証券会社のアプリで全株を売却した", new DateTimeOffset(2026, 9, 19, 1, 5, 0, TimeSpan.Zero),
            AuthorizedBy: "ai-stock-trading-owner");

        var roundTripped = JsonSerializer.Deserialize<PositionDriftAdopted>(JsonSerializer.Serialize(original, Web), Web);

        roundTripped.Should().Be(original);
    }

    [Fact]
    public void 既存の位置引数の並びは変わらない_AuthorizedBy_は末尾の任意引数である()
    {
        var parameters = typeof(PositionDriftAdopted).GetConstructors()
            .Single(c => c.GetParameters().Length > 1).GetParameters();

        parameters.Select(p => p.Name).Should().Equal(
            "AdoptionId", "Symbol", "Market", "LedgerQuantityBefore", "LedgerQuantityAfter", "BrokerQuantity",
            "ObservedAt", "CostBasisPrice", "RealizedPnlRecorded", "ReferencePrice", "EstimatedPnlInBase",
            "Actor", "Reason", "AdoptedAt", "AuthorizedBy");
        parameters[^1].HasDefaultValue.Should().BeTrue();
        parameters[^1].DefaultValue.Should().BeNull();
    }
}
