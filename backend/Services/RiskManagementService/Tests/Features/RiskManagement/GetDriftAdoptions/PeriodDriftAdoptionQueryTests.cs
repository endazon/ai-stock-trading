using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.GetDriftAdoptions;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-06, FR-11, FR-16, ADR-0041 決定 1, #870, #859, IADR-0360 決定 2:
// 期間の乖離の取り込みを絞る純関数（GET /risk-controls/drift-adoptions の実体）。
public class PeriodDriftAdoptionQueryTests
{
    private static LedgerDriftAdoption Adoption(
        DateTimeOffset adoptedAt,
        Market market = Market.UnitedStates,
        string symbol = "AAPL") =>
        new(Guid.NewGuid(), symbol, market, TradeSide.Sell, 10, CostBasisPrice: 1m, FxRateToBase: 1m, LedgerQuantityBefore: 10, BrokerQuantity: 0,
            adoptedAt.AddMinutes(-10), "owner", "アプリから直接売却", adoptedAt);

    // T-10-540: 取引日で絞る。境界は**取り込みの市場の現地取引日**であり、期間約定の照会と同じ規則である。
    [Fact]
    public void 取引日が期間に入る取り込みだけを返す()
    {
        // 米国市場。UTC 2026-09-18 01:00 は ET 2026-09-17 21:00（前日の取引日）。
        var inside = Adoption(new DateTimeOffset(2026, 9, 18, 13, 30, 0, TimeSpan.Zero));
        var before = Adoption(new DateTimeOffset(2026, 9, 10, 13, 30, 0, TimeSpan.Zero));

        var rows = PeriodDriftAdoptionQuery.InTradingDayRange(
            [inside, before], new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 18));

        rows.Should().ContainSingle().Which.AdoptionId.Should().Be(inside.Id);
    }

    // T-10-541: 由来は wire にも出る（後から記録上で区別できることが目的）。価格は運ばない。
    [Fact]
    public void 由来と実現損益未記録を運び_価格は運ばない()
    {
        var rows = PeriodDriftAdoptionQuery.InTradingDayRange(
            [Adoption(new DateTimeOffset(2026, 9, 18, 13, 30, 0, TimeSpan.Zero))],
            new DateOnly(2026, 9, 18), new DateOnly(2026, 9, 18));

        var row = rows.Should().ContainSingle().Subject;
        row.Origin.Should().Be(TradeOrigin.ManualAdoption);
        row.RealizedPnlRecorded.Should().BeFalse("🔴 実現損益は不明であり、0 ではない");

        // 🔴 取り込み行の単価は「取り込み時点の平均取得単価」であって約定価格ではない。wire へ出さない。
        typeof(DriftAdoptionView).GetProperties().Select(p => p.Name)
            .Should().NotContain(["CostBasisPrice", "Price", "FxRateToBase"]);
    }

    [Fact]
    public void 逆順の期間は空を返す()
    {
        var rows = PeriodDriftAdoptionQuery.InTradingDayRange(
            [Adoption(new DateTimeOffset(2026, 9, 18, 13, 30, 0, TimeSpan.Zero))],
            new DateOnly(2026, 9, 18), new DateOnly(2026, 9, 14));

        rows.Should().BeEmpty();
    }

    [Fact]
    public void 取り込み日時の昇順で返す()
    {
        var later = Adoption(new DateTimeOffset(2026, 9, 18, 14, 0, 0, TimeSpan.Zero));
        var earlier = Adoption(new DateTimeOffset(2026, 9, 18, 13, 0, 0, TimeSpan.Zero));

        var rows = PeriodDriftAdoptionQuery.InTradingDayRange(
            [later, earlier], new DateOnly(2026, 9, 18), new DateOnly(2026, 9, 18));

        rows.Select(r => r.AdoptionId).Should().Equal(earlier.Id, later.Id);
    }
}
